using System.Text.RegularExpressions;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>与总线无关的 BlueZ 对象树查询（适配器、设备路径、GATT）。</summary>
internal static class BluezObjectModel
{
    private static readonly Regex MacAddress = new(
        @"^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DeviceSuffix = new(
        @"/dev_([0-9A-Fa-f]{2})_([0-9A-Fa-f]{2})_([0-9A-Fa-f]{2})_([0-9A-Fa-f]{2})_([0-9A-Fa-f]{2})_([0-9A-Fa-f]{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>按路径排序后取第一个 Adapter1；preferredAdapterName 如 hci1 时优先匹配该段。</summary>
    public static string? FindAdapterPath(IEnumerable<BluezObject> objects, string? preferredAdapterName = null)
    {
        var adapters = objects
            .Where(o => HasInterface(o, BluezNames.Adapter1))
            .OrderBy(o => o.Path, StringComparer.Ordinal)
            .ToList();
        if (adapters.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(preferredAdapterName))
        {
            var match = adapters.FirstOrDefault(o => AdapterNameMatches(o.Path, preferredAdapterName));
            return match?.Path;
        }

        return adapters[0].Path;
    }

    /// <summary>设备 id（AA:BB:CC:DD:EE:FF）转为 &lt;adapterPath&gt;/dev_AA_BB_CC_DD_EE_FF。非法 MAC 抛 ArgumentException。</summary>
    public static string DeviceIdToPath(string adapterPath, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(adapterPath) || adapterPath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Adapter path is invalid.", nameof(adapterPath));
        if (deviceId is null || !MacAddress.IsMatch(deviceId))
            throw new ArgumentException("Device id must be a MAC address.", nameof(deviceId));

        var suffix = deviceId.Replace(':', '_').ToUpperInvariant();
        return adapterPath.TrimEnd('/') + "/dev_" + suffix;
    }

    /// <summary>从对象路径还原大写 MAC。路径不含合法设备段时抛 ArgumentException。</summary>
    public static string PathToDeviceId(string path)
    {
        var match = path is null ? Match.Empty : DeviceSuffix.Match(path);
        if (!match.Success)
            throw new ArgumentException("Object path has no device address.", nameof(path));

        return string.Join(':', Enumerable.Range(1, 6).Select(i => match.Groups[i].Value.ToUpperInvariant()));
    }

    /// <summary>列出属于该设备的 GATT 服务及其特征（按对象路径排序）。</summary>
    public static IReadOnlyList<GattServiceInfo> GetServices(IEnumerable<BluezObject> objects, string devicePath)
    {
        var list = objects as IReadOnlyList<BluezObject> ?? objects.ToList();
        var services = list
            .Where(o => HasInterface(o, BluezNames.GattService1) && BelongsToDevice(o, devicePath))
            .OrderBy(o => o.Path, StringComparer.Ordinal)
            .ToList();

        var result = new List<GattServiceInfo>(services.Count);
        foreach (var service in services)
        {
            if (!TryInterface(service, BluezNames.GattService1, out var props))
                continue;
            if (!BluezProps.TryGetString(props, "UUID", out var uuidText) || !TryParseUuid(uuidText, out var serviceUuid))
                continue;

            var characteristics = new List<GattCharacteristicInfo>();
            foreach (var candidate in list.OrderBy(o => o.Path, StringComparer.Ordinal))
            {
                if (!HasInterface(candidate, BluezNames.GattCharacteristic1))
                    continue;
                if (!BelongsToService(candidate, service.Path))
                    continue;
                if (!TryInterface(candidate, BluezNames.GattCharacteristic1, out var charProps))
                    continue;
                if (!BluezProps.TryGetString(charProps, "UUID", out var charUuidText) || !TryParseUuid(charUuidText, out var charUuid))
                    continue;

                var flags = BluezProps.TryGetStrings(charProps, "Flags", out var flagTexts) ? flagTexts : [];
                characteristics.Add(new GattCharacteristicInfo(charUuid, ParseFlags(flags)));
            }

            result.Add(new GattServiceInfo(serviceUuid, characteristics));
        }

        return result;
    }

    /// <summary>按服务 UUID 与特征 UUID 查找特征对象路径。</summary>
    public static string? FindCharacteristicPath(
        IEnumerable<BluezObject> objects,
        string devicePath,
        Guid service,
        Guid characteristic)
    {
        var list = objects as IReadOnlyList<BluezObject> ?? objects.ToList();
        var services = list
            .Where(o => HasInterface(o, BluezNames.GattService1) && BelongsToDevice(o, devicePath))
            .OrderBy(o => o.Path, StringComparer.Ordinal);

        foreach (var svc in services)
        {
            if (!TryInterface(svc, BluezNames.GattService1, out var props))
                continue;
            if (!BluezProps.TryGetString(props, "UUID", out var uuidText) || !TryParseUuid(uuidText, out var serviceUuid))
                continue;
            if (serviceUuid != service)
                continue;

            foreach (var candidate in list.OrderBy(o => o.Path, StringComparer.Ordinal))
            {
                if (!HasInterface(candidate, BluezNames.GattCharacteristic1) || !BelongsToService(candidate, svc.Path))
                    continue;
                if (!TryInterface(candidate, BluezNames.GattCharacteristic1, out var charProps))
                    continue;
                if (!BluezProps.TryGetString(charProps, "UUID", out var charText) || !TryParseUuid(charText, out var charUuid))
                    continue;
                if (charUuid == characteristic)
                    return candidate.Path;
            }
        }

        return null;
    }

    /// <summary>BlueZ Flags 字符串映射为 GattCharacteristicProperties。</summary>
    public static GattCharacteristicProperties ParseFlags(string[] flags)
    {
        if (flags is null || flags.Length == 0)
            return GattCharacteristicProperties.None;

        var value = GattCharacteristicProperties.None;
        foreach (var flag in flags)
        {
            if (flag is null)
                continue;
            value |= flag.Trim().ToLowerInvariant() switch
            {
                "read" => GattCharacteristicProperties.Read,
                "write" => GattCharacteristicProperties.Write,
                "write-without-response" => GattCharacteristicProperties.WriteWithoutResponse,
                "notify" => GattCharacteristicProperties.Notify,
                "indicate" => GattCharacteristicProperties.Indicate,
                _ => GattCharacteristicProperties.None,
            };
        }

        return value;
    }

    /// <summary>解析 BlueZ 128 位 UUID 字符串（大小写不敏感）。</summary>
    public static bool TryParseUuid(string? text, out Guid uuid)
    {
        uuid = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return Guid.TryParse(text, out uuid);
    }

    private static bool HasInterface(BluezObject obj, string interfaceName) =>
        obj.Interfaces is not null && obj.Interfaces.ContainsKey(interfaceName);

    private static bool TryInterface(BluezObject obj, string interfaceName, out IReadOnlyDictionary<string, object?> props)
    {
        props = EmptyProps;
        if (obj.Interfaces is null)
            return false;
        return obj.Interfaces.TryGetValue(interfaceName, out props!) && props is not null;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyProps =
        new Dictionary<string, object?>();

    private static bool AdapterNameMatches(string path, string preferred)
    {
        if (string.Equals(path, preferred, StringComparison.Ordinal))
            return true;
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return string.Equals(name, preferred, StringComparison.Ordinal);
    }

    private static bool BelongsToDevice(BluezObject service, string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath) || service.Path is null)
            return false;
        if (TryInterface(service, BluezNames.GattService1, out var props)
            && BluezProps.TryGetString(props, "Device", out var device)
            && device.Length > 0)
        {
            return string.Equals(device, devicePath, StringComparison.Ordinal);
        }

        return service.Path.StartsWith(devicePath + "/", StringComparison.Ordinal);
    }

    private static bool BelongsToService(BluezObject characteristic, string servicePath)
    {
        if (string.IsNullOrEmpty(servicePath) || characteristic.Path is null)
            return false;
        if (TryInterface(characteristic, BluezNames.GattCharacteristic1, out var props)
            && BluezProps.TryGetString(props, "Service", out var service)
            && service.Length > 0)
        {
            return string.Equals(service, servicePath, StringComparison.Ordinal);
        }

        return characteristic.Path.StartsWith(servicePath + "/", StringComparison.Ordinal);
    }
}
