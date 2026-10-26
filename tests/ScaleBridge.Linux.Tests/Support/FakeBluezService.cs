using System.Collections.Concurrent;
using Tmds.DBus.Protocol;

namespace ScaleBridge.Linux.Tests.Support;

internal delegate void WireBody(ref MessageWriter writer);

/// <summary>
/// 一个跑在私有 dbus-daemon 上的最小 org.bluez 假服务（用 Tmds.DBus.Protocol 手写），
/// 用来真实走一遍 TmdsBluezBus 的线上编解码。它不是 BlueZ，只按 BlueZ 文档的签名回复。
/// </summary>
internal sealed class FakeBluezService : IPathMethodHandler
{
    public const string Adapter = "/org/bluez/hci0";
    public const string DeviceMac = "AA:BB:CC:DD:EE:01";
    public const string Device = "/org/bluez/hci0/dev_AA_BB_CC_DD_EE_01";
    public const string FailingDevice = "/org/bluez/hci0/dev_FF_FF_FF_FF_FF_FF";
    public const string Service = Device + "/service0010";
    public const string Characteristic = Service + "/char0011";
    public const string ServiceUuid = "0000181d-0000-1000-8000-00805f9b34fb";
    public const string CharacteristicUuid = "00002a9d-0000-1000-8000-00805f9b34fb";

    private volatile bool _connected;

    public ConcurrentQueue<string> Calls { get; } = new();

    public string? FilterTransport { get; private set; }

    public string[]? FilterUuids { get; private set; }

    public bool? FilterDuplicateData { get; private set; }

    public string Path => "/";

    public bool HandlesChildPaths => true;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        var path = request.PathAsString ?? string.Empty;
        var iface = request.InterfaceAsString ?? string.Empty;
        var member = request.MemberAsString ?? string.Empty;

        switch (iface, member)
        {
            case ("org.freedesktop.DBus.ObjectManager", "GetManagedObjects") when path == "/":
                Reply(context, "a{oa{sa{sv}}}", WriteManagedObjects);
                break;
            case ("org.bluez.Adapter1", "StartDiscovery" or "StopDiscovery") when path == Adapter:
                Calls.Enqueue(member);
                Reply(context, null, null);
                break;
            case ("org.bluez.Adapter1", "SetDiscoveryFilter") when path == Adapter:
                Calls.Enqueue(member);
                var reader = request.GetBodyReader();
                var options = reader.ReadDictionaryOfStringToVariantValue();
                FilterTransport = options.TryGetValue("Transport", out var transport) ? transport.GetString() : null;
                FilterUuids = options.TryGetValue("UUIDs", out var uuids) ? uuids.GetArray<string>() : null;
                FilterDuplicateData = options.TryGetValue("DuplicateData", out var dup) ? dup.GetBool() : null;
                Reply(context, null, null);
                break;
            case ("org.bluez.Device1", "Connect") when path == FailingDevice:
                Calls.Enqueue("Connect(fail)");
                context.ReplyError("org.bluez.Error.NotReady", "Resource Not Ready");
                break;
            case ("org.bluez.Device1", "Connect" or "Disconnect"):
                Calls.Enqueue(member);
                _connected = member == "Connect";
                Reply(context, null, null);
                // 像真实 BlueZ 一样：Connected 变化通过 PropertiesChanged 通知。
                EmitDeviceConnected(context.Connection, path, _connected);
                break;
            case ("org.bluez.GattCharacteristic1", "ReadValue"):
                Calls.Enqueue(member);
                Reply(context, "ay", static (ref MessageWriter w) => w.WriteArray(new byte[] { 1, 2, 3 }));
                break;
            case ("org.bluez.GattCharacteristic1", "StartNotify" or "StopNotify"):
                Calls.Enqueue(member);
                Reply(context, null, null);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return default;
    }

    public static void EmitCharacteristicValue(DBusConnection connection, byte[] value)
    {
        var w = connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(
                path: Characteristic,
                @interface: "org.freedesktop.DBus.Properties",
                member: "PropertiesChanged",
                signature: "sa{sv}as");
            w.WriteString("org.bluez.GattCharacteristic1");
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("Value");
            w.WriteSignature("ay");
            w.WriteArray(value);
            w.WriteDictionaryEnd(dict);
            w.WriteArray(Array.Empty<string>());
            connection.TrySendMessage(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
    }

    public static void EmitDeviceConnected(DBusConnection connection, string devicePath, bool connected)
    {
        var w = connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(
                path: devicePath,
                @interface: "org.freedesktop.DBus.Properties",
                member: "PropertiesChanged",
                signature: "sa{sv}as");
            w.WriteString("org.bluez.Device1");
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("Connected");
            w.WriteVariantBool(connected);
            w.WriteDictionaryEnd(dict);
            w.WriteArray(new[] { "RSSI" });
            connection.TrySendMessage(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
    }

    public static void EmitInterfacesAdded(DBusConnection connection)
    {
        var w = connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(
                path: "/",
                @interface: "org.freedesktop.DBus.ObjectManager",
                member: "InterfacesAdded",
                signature: "oa{sa{sv}}");
            w.WriteObjectPath(FailingDevice);
            WriteInterfaces(ref w, [("org.bluez.Device1", WriteFailingDeviceProps)]);
            connection.TrySendMessage(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
    }

    public static void EmitInterfacesRemoved(DBusConnection connection)
    {
        var w = connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(
                path: "/",
                @interface: "org.freedesktop.DBus.ObjectManager",
                member: "InterfacesRemoved",
                signature: "oas");
            w.WriteObjectPath(FailingDevice);
            w.WriteArray(new[] { "org.bluez.Device1" });
            connection.TrySendMessage(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
    }

    private static void Reply(MethodContext context, string? signature, WireBody? body)
    {
        var w = context.CreateReplyWriter(signature);
        try
        {
            body?.Invoke(ref w);
            context.Reply(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
    }

    private void WriteManagedObjects(ref MessageWriter w)
    {
        var all = w.WriteDictionaryStart();
        WriteObject(ref w, Adapter, [("org.bluez.Adapter1", WriteAdapterProps)]);
        WriteObject(ref w, Device, [("org.bluez.Device1", WriteDeviceProps)]);
        WriteObject(ref w, Service, [("org.bluez.GattService1", WriteServiceProps)]);
        WriteObject(ref w, Characteristic, [("org.bluez.GattCharacteristic1", WriteCharacteristicProps)]);
        w.WriteDictionaryEnd(all);
    }

    private static void WriteObject(ref MessageWriter w, string path, (string Name, WireBody Props)[] interfaces)
    {
        w.WriteDictionaryEntryStart();
        w.WriteObjectPath(path);
        WriteInterfaces(ref w, interfaces);
    }

    private static void WriteInterfaces(ref MessageWriter w, (string Name, WireBody Props)[] interfaces)
    {
        var start = w.WriteDictionaryStart();
        foreach (var (name, props) in interfaces)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(name);
            props(ref w);
        }

        w.WriteDictionaryEnd(start);
    }

    private static void Prop(ref MessageWriter w, string key)
    {
        w.WriteDictionaryEntryStart();
        w.WriteString(key);
    }

    private static void WriteAdapterProps(ref MessageWriter w)
    {
        var d = w.WriteDictionaryStart();
        Prop(ref w, "Address");
        w.WriteVariantString("00:11:22:33:44:55");
        Prop(ref w, "Name");
        w.WriteVariantString("hci0");
        Prop(ref w, "Powered");
        w.WriteVariantBool(true);
        w.WriteDictionaryEnd(d);
    }

    private static void WriteFailingDeviceProps(ref MessageWriter w)
    {
        var d = w.WriteDictionaryStart();
        Prop(ref w, "Address");
        w.WriteVariantString("FF:FF:FF:FF:FF:FF");
        Prop(ref w, "Adapter");
        w.WriteVariantObjectPath(Adapter);
        w.WriteDictionaryEnd(d);
    }

    private void WriteDeviceProps(ref MessageWriter w)
    {
        var d = w.WriteDictionaryStart();
        Prop(ref w, "Address");
        w.WriteVariantString(DeviceMac);
        Prop(ref w, "Alias");
        w.WriteVariantString("Test Scale");
        Prop(ref w, "RSSI");
        w.WriteVariantInt16(-61);
        Prop(ref w, "Connected");
        w.WriteVariantBool(_connected);
        Prop(ref w, "ServicesResolved");
        w.WriteVariantBool(true);
        Prop(ref w, "Adapter");
        w.WriteVariantObjectPath(Adapter);
        Prop(ref w, "Class");
        w.WriteVariantUInt32(0x1234);
        Prop(ref w, "Weird");
        w.WriteVariantDouble(1.5);
        Prop(ref w, "UUIDs");
        w.WriteSignature("as");
        w.WriteArray(new[] { ServiceUuid });
        Prop(ref w, "ManufacturerData");
        w.WriteSignature("a{qv}");
        var m = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart();
        w.WriteUInt16(0x004C);
        w.WriteSignature("ay");
        w.WriteArray(new byte[] { 0x10, 0x05 });
        w.WriteDictionaryEnd(m);
        Prop(ref w, "ServiceData");
        w.WriteSignature("a{sv}");
        var s = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart();
        w.WriteString(ServiceUuid);
        w.WriteSignature("ay");
        w.WriteArray(new byte[] { 0xAA });
        w.WriteDictionaryEnd(s);
        w.WriteDictionaryEnd(d);
    }

    private static void WriteServiceProps(ref MessageWriter w)
    {
        var d = w.WriteDictionaryStart();
        Prop(ref w, "UUID");
        w.WriteVariantString(ServiceUuid);
        Prop(ref w, "Device");
        w.WriteVariantObjectPath(Device);
        Prop(ref w, "Primary");
        w.WriteVariantBool(true);
        w.WriteDictionaryEnd(d);
    }

    private static void WriteCharacteristicProps(ref MessageWriter w)
    {
        var d = w.WriteDictionaryStart();
        Prop(ref w, "UUID");
        w.WriteVariantString(CharacteristicUuid);
        Prop(ref w, "Service");
        w.WriteVariantObjectPath(Service);
        Prop(ref w, "Flags");
        w.WriteSignature("as");
        w.WriteArray(new[] { "indicate" });
        w.WriteDictionaryEnd(d);
    }
}
