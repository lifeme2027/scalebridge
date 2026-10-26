using System.Text.RegularExpressions;
using ScaleBridge.Core.Ble;

namespace ScaleBridge.Linux.Bluez;

/// <summary>按对象路径缓存 Device1 属性增量，并组装 AdvertisedDevice。</summary>
internal sealed class BluezAdvertisementCache
{
    private static readonly Regex MacAddress = new(
        @"^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> RelevantKeys = new(StringComparer.Ordinal)
    {
        "Name", "RSSI", "ManufacturerData", "ServiceData", "UUIDs", "Address",
    };

    private readonly object _gate = new();
    private readonly string _adapterPath;
    private readonly Dictionary<string, Dictionary<string, object?>> _devices = new(StringComparer.Ordinal);

    /// <summary>只接受 Adapter 属性或路径前缀等于 adapterPath 的设备。</summary>
    public BluezAdvertisementCache(string adapterPath)
    {
        _adapterPath = NormalizePath(adapterPath);
    }

    /// <summary>用完整 Device1 快照覆盖缓存。仅相关属性变化时返回 true。</summary>
    public bool Apply(BluezObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        lock (_gate)
        {
            if (obj.Interfaces is null || !obj.Interfaces.TryGetValue(BluezNames.Device1, out var props) || props is null)
                return false;
            if (!Belongs(obj.Path, props, existing: null))
                return false;

            var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
            var relevant = false;
            foreach (var pair in props)
            {
                entry[pair.Key] = Snapshot(pair.Key, pair.Value);
                if (RelevantKeys.Contains(pair.Key))
                    relevant = true;
            }

            _devices[obj.Path] = entry;
            return relevant;
        }
    }

    /// <summary>合并 PropertiesChanged。Invalidated 中的名字会删掉缓存属性。仅相关属性变化时返回 true。</summary>
    public bool Apply(BluezPropertiesChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!string.Equals(change.Interface, BluezNames.Device1, StringComparison.Ordinal))
            return false;

        lock (_gate)
        {
            _devices.TryGetValue(change.Path, out var existing);
            if (!Belongs(change.Path, change.Changed, existing))
                return false;

            var entry = existing ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            var relevant = false;
            if (change.Changed is not null)
            {
                foreach (var pair in change.Changed)
                {
                    entry[pair.Key] = Snapshot(pair.Key, pair.Value);
                    if (RelevantKeys.Contains(pair.Key))
                        relevant = true;
                }
            }

            if (change.Invalidated is not null)
            {
                foreach (var name in change.Invalidated)
                {
                    if (name is null)
                        continue;
                    var removed = entry.Remove(name);
                    if (removed && RelevantKeys.Contains(name))
                        relevant = true;
                }
            }

            _devices[change.Path] = entry;
            return relevant;
        }
    }

    /// <summary>丢掉该路径上的缓存。</summary>
    public void Remove(string path)
    {
        lock (_gate)
            _devices.Remove(path);
    }

    /// <summary>由缓存组装广播。地址缺失或非法时返回 false。厂商数据的字节数组会复制。</summary>
    public bool TryBuild(string path, DateTimeOffset seenAt, out AdvertisedDevice device)
    {
        lock (_gate)
        {
            device = null!;
            if (!_devices.TryGetValue(path, out var entry))
                return false;
            if (!Belongs(path, incoming: null, entry))
                return false;
            if (!BluezProps.TryGetString(entry, "Address", out var address) || !MacAddress.IsMatch(address))
                return false;

            int? rssi = BluezProps.TryGetInt16(entry, "RSSI", out var rssiValue) ? rssiValue : null;
            string? name = BluezProps.TryGetString(entry, "Name", out var nameValue) ? nameValue : null;
            var manufacturer = CopyManufacturer(entry);
            device = new AdvertisedDevice(
                address.ToUpperInvariant(),
                name,
                rssi,
                CollectUuids(entry),
                manufacturer,
                seenAt,
                IsDemo: false);
            return true;
        }
    }

    /// <summary>服务过滤：null 或空表示全部匹配，否则与缓存中的服务 UUID 有交集才匹配。</summary>
    public bool Matches(string path, IReadOnlyList<Guid>? filter)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(path, out var entry))
                return false;
            if (!Belongs(path, incoming: null, entry))
                return false;
            if (filter is null || filter.Count == 0)
                return true;

            foreach (var uuid in CollectUuids(entry))
            {
                if (filter.Contains(uuid))
                    return true;
            }

            return false;
        }
    }

    private bool Belongs(string path, IReadOnlyDictionary<string, object?>? incoming, Dictionary<string, object?>? existing)
    {
        if (incoming is not null && BluezProps.TryGetString(incoming, "Adapter", out var announced))
            return AdapterEquals(announced);
        if (existing is not null && BluezProps.TryGetString(existing, "Adapter", out var cached))
            return AdapterEquals(cached);
        return PathBelongs(path);
    }

    private bool AdapterEquals(string adapter) =>
        string.Equals(NormalizePath(adapter), _adapterPath, StringComparison.Ordinal);

    private bool PathBelongs(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var normalized = path.TrimEnd('/');
        return normalized.StartsWith(_adapterPath + "/", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path) => (path ?? "").TrimEnd('/');

    private static IReadOnlyList<Guid> CollectUuids(IReadOnlyDictionary<string, object?> entry)
    {
        var list = new List<Guid>();
        var seen = new HashSet<Guid>();
        if (BluezProps.TryGetStrings(entry, "UUIDs", out var uuids))
        {
            foreach (var text in uuids)
                AddUuid(list, seen, text);
        }

        if (BluezProps.TryGetServiceData(entry, "ServiceData", out var serviceData))
        {
            foreach (var key in serviceData.Keys)
                AddUuid(list, seen, key);
        }

        return list;
    }

    private static void AddUuid(List<Guid> list, HashSet<Guid> seen, string? text)
    {
        if (!BluezObjectModel.TryParseUuid(text, out var uuid) || !seen.Add(uuid))
            return;
        list.Add(uuid);
    }

    private static IReadOnlyDictionary<ushort, byte[]> CopyManufacturer(IReadOnlyDictionary<string, object?> entry)
    {
        if (!BluezProps.TryGetManufacturerData(entry, "ManufacturerData", out var data) || data.Count == 0)
            return new Dictionary<ushort, byte[]>();

        var copy = new Dictionary<ushort, byte[]>(data.Count);
        foreach (var pair in data)
            copy[pair.Key] = pair.Value is null ? [] : (byte[])pair.Value.Clone();
        return copy;
    }

    private static object? Snapshot(string key, object? value)
    {
        if (value is null)
            return null;

        if (string.Equals(key, "ManufacturerData", StringComparison.Ordinal))
        {
            var wrapped = new Dictionary<string, object?> { [key] = value };
            if (BluezProps.TryGetManufacturerData(wrapped, key, out var data))
            {
                var copy = new Dictionary<ushort, byte[]>(data.Count);
                foreach (var pair in data)
                    copy[pair.Key] = pair.Value is null ? [] : (byte[])pair.Value.Clone();
                return copy;
            }
        }

        if (string.Equals(key, "ServiceData", StringComparison.Ordinal))
        {
            var wrapped = new Dictionary<string, object?> { [key] = value };
            if (BluezProps.TryGetServiceData(wrapped, key, out var data))
            {
                var copy = new Dictionary<string, byte[]>(data.Count, StringComparer.Ordinal);
                foreach (var pair in data)
                    copy[pair.Key] = pair.Value is null ? [] : (byte[])pair.Value.Clone();
                return copy;
            }
        }

        if (string.Equals(key, "UUIDs", StringComparison.Ordinal) && value is string[] texts)
            return texts.ToArray();

        if (value is byte[] bytes)
            return bytes.ToArray();

        return value;
    }
}
