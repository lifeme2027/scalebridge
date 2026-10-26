using System.Net.Sockets;
using Tmds.DBus.Protocol;

namespace ScaleBridge.Linux.Bluez;

/// <summary>
/// 基于 Tmds.DBus.Protocol 的真实 IBluezBus（手写代理，不用 Generator，不使用 AcquireNotify/文件描述符）。
/// 实验性：仅按 BlueZ 公开文档实现，未在真实 BlueZ 上验证；见 NOTES。
/// </summary>
internal sealed class TmdsBluezBus : IBluezBus
{
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string NoServer = "org.freedesktop.DBus.Error.NoServer";
    private const string AccessDenied = "org.freedesktop.DBus.Error.AccessDenied";
    private const string Disconnected = "org.freedesktop.DBus.Error.Disconnected";

    private delegate void WriteBody(ref MessageWriter writer);

    private readonly DBusConnection _connection;
    private int _disposed;

    private TmdsBluezBus(DBusConnection connection)
    {
        _connection = connection;
    }

    public event Action<Exception?>? ConnectionClosed;

    /// <summary>连接系统总线（address 为 null 时用 DBUS_SYSTEM_BUS_ADDRESS 或默认套接字）。失败抛 BluezDBusException。</summary>
    public static async Task<TmdsBluezBus> ConnectAsync(string? address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = address ?? DBusAddress.System;
        if (string.IsNullOrEmpty(target))
            throw new BluezDBusException(NoServer, "no system bus address");

        DBusConnection? connection = null;
        try
        {
            connection = new DBusConnection(target);
            await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            throw TranslateConnectFailure(ex);
        }

        var bus = new TmdsBluezBus(connection);
        _ = bus.MonitorAsync();
        return bus;
    }

    public async Task<IReadOnlyList<BluezObject>> GetManagedObjectsAsync(CancellationToken ct) =>
        await CallAsync("/", ObjectManagerInterface, "GetManagedObjects", null, null, static (message, _) => ReadManagedObjects(message), ct)
            .ConfigureAwait(false);

    public Task SetDiscoveryFilterAsync(string adapterPath, BluezDiscoveryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var options = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["Transport"] = filter.Transport,
            ["DuplicateData"] = filter.DuplicateData,
        };
        if (filter.Uuids.Count > 0)
            options["UUIDs"] = VariantValue.Array(filter.Uuids.ToArray());

        return CallVoidAsync(
            adapterPath,
            BluezNames.Adapter1,
            "SetDiscoveryFilter",
            "a{sv}",
            (ref MessageWriter w) => w.WriteDictionary(options),
            ct);
    }

    public Task StartDiscoveryAsync(string adapterPath, CancellationToken ct) =>
        CallVoidAsync(adapterPath, BluezNames.Adapter1, "StartDiscovery", null, null, ct);

    public Task StopDiscoveryAsync(string adapterPath, CancellationToken ct) =>
        CallVoidAsync(adapterPath, BluezNames.Adapter1, "StopDiscovery", null, null, ct);

    public Task ConnectDeviceAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(devicePath, BluezNames.Device1, "Connect", null, null, ct);

    public Task DisconnectDeviceAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(devicePath, BluezNames.Device1, "Disconnect", null, null, ct);

    public Task<byte[]> ReadValueAsync(string characteristicPath, CancellationToken ct) =>
        CallAsync(
            characteristicPath,
            BluezNames.GattCharacteristic1,
            "ReadValue",
            "a{sv}",
            (ref MessageWriter w) => w.WriteDictionary(new Dictionary<string, VariantValue>()),
            static (message, _) => message.GetBodyReader().ReadArrayOfByte(),
            ct);

    public Task StartNotifyAsync(string characteristicPath, CancellationToken ct) =>
        CallVoidAsync(characteristicPath, BluezNames.GattCharacteristic1, "StartNotify", null, null, ct);

    public Task StopNotifyAsync(string characteristicPath, CancellationToken ct) =>
        CallVoidAsync(characteristicPath, BluezNames.GattCharacteristic1, "StopNotify", null, null, ct);

    public Task<IDisposable> WatchInterfacesAddedAsync(Action<BluezObject> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var rule = Rule(ObjectManagerInterface, "InterfacesAdded");
        return AddWatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return ReadObjectEntry(ref reader);
            },
            handler,
            ct);
    }

    public Task<IDisposable> WatchInterfacesRemovedAsync(Action<string, IReadOnlyList<string>> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var rule = Rule(ObjectManagerInterface, "InterfacesRemoved");
        return AddWatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var path = reader.ReadObjectPathAsString();
                IReadOnlyList<string> interfaces = reader.ReadArrayOfString();
                return (Path: path, Interfaces: interfaces);
            },
            removed => handler(removed.Path, removed.Interfaces),
            ct);
    }

    public Task<IDisposable> WatchPropertiesChangedAsync(string? path, Action<BluezPropertiesChanged> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var rule = Rule(PropertiesInterface, "PropertiesChanged");
        if (path is null)
            rule.PathNamespace = BluezNames.RootPath;
        else
            rule.Path = path;

        return AddWatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var iface = reader.ReadString();
                var changed = BluezVariantConverter.ToProperties(reader.ReadDictionaryOfStringToVariantValue());
                IReadOnlyList<string> invalidated = reader.ReadArrayOfString();
                return new BluezPropertiesChanged(message.PathAsString ?? string.Empty, iface, changed, invalidated);
            },
            handler,
            ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _connection.Dispose();
            }
            catch
            {
                // best effort
            }
        }

        return ValueTask.CompletedTask;
    }

    private static MatchRule Rule(string iface, string member) => new()
    {
        Type = MessageType.Signal,
        Sender = BluezNames.Service,
        Interface = iface,
        Member = member,
    };

    private async Task MonitorAsync()
    {
        Exception? error;
        try
        {
            error = await _connection.DisconnectedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            ConnectionClosed?.Invoke(error ?? new BluezDBusException(Disconnected, "connection closed"));
        }
        catch
        {
            // subscribers must not throw
        }
    }

    private async Task<IDisposable> AddWatchAsync<T>(
        MatchRule rule,
        MessageValueReader<T> reader,
        Action<T> handler,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        void OnNotification(Notification<T> notification)
        {
            if (!notification.HasValue)
                return;
            try
            {
                handler(notification.Value);
            }
            catch
            {
                // A throwing handler would tear down the connection; swallow it.
            }
        }

        // 不捕获 SynchronizationContext：回调在连接的接收线程上直接执行（处理器只写 Channel）。
        var pending = _connection
            .AddMatchAsync(rule, reader, OnNotification, (SynchronizationContext?)null, ObserverFlags.None, null)
            .AsTask();
        try
        {
            var subscription = await pending.WaitAsync(ct).ConfigureAwait(false);
            return new Subscription(subscription);
        }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    private Task CallVoidAsync(string path, string iface, string member, string? signature, WriteBody? body, CancellationToken ct) =>
        CallCoreAsync(path, iface, member, signature, body, ct, static (conn, message) => conn.CallMethodAsync(message));

    private Task<T> CallAsync<T>(
        string path,
        string iface,
        string member,
        string? signature,
        WriteBody? body,
        MessageValueReader<T> reader,
        CancellationToken ct) =>
        CallCoreAsync(path, iface, member, signature, body, ct, (conn, message) => conn.CallMethodAsync(message, reader));

    private async Task<T> CallCoreAsync<T>(
        string path,
        string iface,
        string member,
        string? signature,
        WriteBody? body,
        CancellationToken ct,
        Func<DBusConnection, MessageBuffer, Task<T>> send)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            ThrowIfDisposed();
            var message = CreateCall(path, iface, member, signature, body);
            return await send(_connection, message).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    private Task CallCoreAsync(
        string path,
        string iface,
        string member,
        string? signature,
        WriteBody? body,
        CancellationToken ct,
        Func<DBusConnection, MessageBuffer, Task> send) =>
        CallCoreAsync<object?>(
            path,
            iface,
            member,
            signature,
            body,
            ct,
            async (conn, message) =>
            {
                await send(conn, message).ConfigureAwait(false);
                return null;
            });

    private MessageBuffer CreateCall(string path, string iface, string member, string? signature, WriteBody? body)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: BluezNames.Service,
                path: path,
                @interface: iface,
                member: member,
                signature: signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new BluezDBusException(Disconnected, "bus is closed");
    }

    private static IReadOnlyList<BluezObject> ReadManagedObjects(Message message)
    {
        var reader = message.GetBodyReader();
        var objects = new List<BluezObject>();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
            objects.Add(ReadObjectEntry(ref reader));
        return objects;
    }

    /// <summary>读取 o + a{sa{sv}}（GetManagedObjects 的一项，也是 InterfacesAdded 的消息体）。</summary>
    private static BluezObject ReadObjectEntry(ref Reader reader)
    {
        var path = reader.ReadObjectPathAsString();
        var interfaces = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            var name = reader.ReadString();
            interfaces[name] = BluezVariantConverter.ToProperties(reader.ReadDictionaryOfStringToVariantValue());
        }

        return new BluezObject(path, interfaces);
    }

    private static BluezDBusException TranslateConnectFailure(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException!)
        {
            if (inner is UnauthorizedAccessException || inner is SocketException { SocketErrorCode: SocketError.AccessDenied })
                return new BluezDBusException(AccessDenied, "access to the system bus was denied");
            if (inner.InnerException is null)
                break;
        }

        return new BluezDBusException(NoServer, "cannot connect to the system bus");
    }

    private static Exception Translate(Exception ex) => ex switch
    {
        BluezDBusException => ex,
        DBusErrorReplyException reply => new BluezDBusException(reply.ErrorName, reply.ErrorMessage ?? string.Empty),
        DBusConnectionException => new BluezDBusException(Disconnected, "connection closed"),
        ObjectDisposedException => new BluezDBusException(Disconnected, "bus is closed"),
        _ => new BluezDBusException("org.freedesktop.DBus.Error.Failed", ex.GetType().Name),
    };

    private sealed class Subscription(IDisposable inner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                inner.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }
}
