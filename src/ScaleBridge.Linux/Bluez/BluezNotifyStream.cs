using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>
/// 订阅特征 Value。先挂 PropertiesChanged 再 StartNotify。取消时安静结束。
/// 不使用 AcquireNotify 或文件描述符：Tmds.DBus issue 456，BlueZ 的 notify fd 无法安全传递。
/// </summary>
internal static class BluezNotifyStream
{
    /// <summary>按信号顺序产出 Value 的副本；Invalidated 或类型不符时回退 ReadValue。</summary>
    public static async IAsyncEnumerable<byte[]> SubscribeAsync(
        IBluezBus bus,
        string characteristicPath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentException.ThrowIfNullOrEmpty(characteristicPath);

        var channel = Channel.CreateUnbounded<NotifyWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        IDisposable? watcher = null;
        var notifyStarted = false;
        Exception? fault = null;

        void OnChanged(BluezPropertiesChanged change)
        {
            try
            {
                if (!string.Equals(change.Path, characteristicPath, StringComparison.Ordinal))
                    return;
                if (!string.Equals(change.Interface, BluezNames.GattCharacteristic1, StringComparison.Ordinal))
                    return;

                if (change.Changed is not null && change.Changed.TryGetValue("Value", out var raw))
                {
                    if (raw is byte[] bytes)
                        channel.Writer.TryWrite(NotifyWork.FromValue((byte[])bytes.Clone()));
                    else
                        channel.Writer.TryWrite(NotifyWork.Read());
                    return;
                }

                if (ContainsValue(change.Invalidated))
                    channel.Writer.TryWrite(NotifyWork.Read());
            }
            catch
            {
                // Handlers must not throw or block; a bad signal is dropped.
            }
        }

        void OnClosed(Exception? error)
        {
            try
            {
                channel.Writer.TryComplete(MapClosed(error));
            }
            catch
            {
            }
        }

        bus.ConnectionClosed += OnClosed;
        var stopReading = false;
        try
        {
            try
            {
                watcher = await bus.WatchPropertiesChangedAsync(characteristicPath, OnChanged, ct).ConfigureAwait(false);
                try
                {
                    await bus.StartNotifyAsync(characteristicPath, ct).ConfigureAwait(false);
                    notifyStarted = true;
                }
                catch (BluezDBusException ex) when (string.Equals(ex.ErrorName, "org.bluez.Error.InProgress", StringComparison.Ordinal))
                {
                    notifyStarted = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    watcher.Dispose();
                    watcher = null;
                    throw BluezErrorMapper.Map(ex, "notify");
                }
            }
            catch (OperationCanceledException)
            {
                stopReading = true;
            }
            catch (Exception ex) when (ex is not BleException)
            {
                throw BluezErrorMapper.Map(ex, "notify");
            }

            while (!stopReading)
            {
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException ex)
                {
                    fault = ex.InnerException;
                    break;
                }

                if (!more)
                    break;

                while (channel.Reader.TryRead(out var work))
                {
                    if (!work.NeedsRead)
                    {
                        yield return work.Value;
                        continue;
                    }

                    byte[] read;
                    try
                    {
                        read = await bus.ReadValueAsync(characteristicPath, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
                    {
                        stopReading = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        fault = ex as BleException ?? BluezErrorMapper.Map(ex, "notify");
                        stopReading = true;
                        break;
                    }

                    yield return read is null ? [] : (byte[])read.Clone();
                }
            }
        }
        finally
        {
            bus.ConnectionClosed -= OnClosed;
            watcher?.Dispose();
            if (notifyStarted)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await bus.StopNotifyAsync(characteristicPath, stop.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        if (fault is not null)
            throw fault;
    }

    private static bool ContainsValue(IReadOnlyList<string>? invalidated)
    {
        if (invalidated is null)
            return false;
        for (var i = 0; i < invalidated.Count; i++)
        {
            if (string.Equals(invalidated[i], "Value", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Exception MapClosed(Exception? error)
    {
        if (error is null)
            return new BluetoothUnavailableException("notify failed (connection closed).");
        try
        {
            return BluezErrorMapper.Map(error, "notify");
        }
        catch (ArgumentException)
        {
            return new BluetoothUnavailableException("notify failed (connection closed).");
        }
    }

    private readonly struct NotifyWork
    {
        public bool NeedsRead { get; init; }

        public byte[] Value { get; init; }

        public static NotifyWork FromValue(byte[] value) => new() { Value = value };

        public static NotifyWork Read() => new() { NeedsRead = true, Value = [] };
    }
}
