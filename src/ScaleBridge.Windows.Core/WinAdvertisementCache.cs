using ScaleBridge.Core.Ble;
using ScaleBridge.Windows.Platform;

namespace ScaleBridge.Windows;

/// <summary>
/// 按地址合并广播事件。Windows 的 BluetoothLEAdvertisementWatcher 把「广播包」和「Scan Response」
/// 作为两次独立事件上报（名字常常只在其中一个里），不合并会导致设备名时有时无、服务 UUID 缺失。
/// 容量有上限，超出时丢弃最久没见到的设备，防止长时间扫描时内存增长。
/// </summary>
internal sealed class WinAdvertisementCache
{
    /// <summary>规范里 RSSI = 127 表示「不可用」（Core Spec Vol 4 Part E §7.7.65.2）。</summary>
    internal const int RssiNotAvailable = 127;

    private sealed class Entry
    {
        public string? Name;
        public int? Rssi;
        public DateTimeOffset LastSeen;
        public readonly List<Guid> Services = new();
        public readonly Dictionary<ushort, byte[]> Manufacturer = new();
    }

    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly int _capacity;

    public WinAdvertisementCache(int capacity = 1024)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    /// <summary>合并一条广播并返回该设备当前的完整快照（数组均为拷贝）。</summary>
    public AdvertisedDevice Merge(WinAdvertisement ad, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(ad.Address, out var e))
        {
            if (_entries.Count >= _capacity) EvictOldest();
            e = new Entry();
            _entries[ad.Address] = e;
        }

        e.LastSeen = now;
        if (!string.IsNullOrEmpty(ad.LocalName)) e.Name = ad.LocalName;
        if (ad.Rssi != RssiNotAvailable) e.Rssi = ad.Rssi;
        foreach (var u in ad.ServiceUuids)
            if (!e.Services.Contains(u)) e.Services.Add(u);
        foreach (var m in ad.ManufacturerData)
            e.Manufacturer[m.CompanyId] = (byte[])m.Data.Clone();

        return new AdvertisedDevice(
            WinAddress.Format(ad.Address),
            e.Name,
            e.Rssi,
            e.Services.ToArray(),
            e.Manufacturer.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone()),
            now,
            IsDemo: false);
    }

    private void EvictOldest()
    {
        ulong? oldest = null;
        var oldestSeen = DateTimeOffset.MaxValue;
        foreach (var (addr, entry) in _entries)
        {
            if (entry.LastSeen <= oldestSeen)
            {
                oldestSeen = entry.LastSeen;
                oldest = addr;
            }
        }
        if (oldest is { } a) _entries.Remove(a);
    }
}
