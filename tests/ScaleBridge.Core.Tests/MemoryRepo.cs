using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Tests;

/// <summary>测试用内存仓库：语义与 SQLite 实现一致（显示时间倒序、游标分页、去重键随记录持久、删除/清空时释放键）。</summary>
internal sealed class MemoryRepo : IMeasurementRepository
{
    private readonly List<Measurement> _items = new();
    private readonly Dictionary<string, long> _keys = new(StringComparer.Ordinal);
    private long _id;

    public Measurement Add(Measurement m)
    {
        var s = m with { Id = ++_id };
        _items.Add(s);
        if (MeasurementPolicy.DedupKey(s) is { } k) _keys.TryAdd(k, s.Id);
        return s;
    }

    private IEnumerable<Measurement> Ordered() => _items.OrderByDescending(x => x.DisplayTime).ThenByDescending(x => x.Id);
    public IReadOnlyList<Measurement> List() => Ordered().ToList();
    public int Count() => _items.Count;

    public IReadOnlyList<Measurement> ListPage(int limit, Measurement? after = null) =>
        Ordered().Where(x => after is null || x.DisplayTime < after.DisplayTime || (x.DisplayTime == after.DisplayTime && x.Id < after.Id))
            .Take(Math.Max(0, limit)).ToList();

    public Measurement? Get(long id) => _items.FirstOrDefault(x => x.Id == id);

    public IReadOnlyList<Measurement> FindByDeviceTime(string deviceId, DateTime deviceTime) =>
        _items.Where(x => x.DeviceId == deviceId && x.DeviceTimestamp == deviceTime).ToList();

    public Measurement? Replace(Measurement merged, string? additionalDedupKey)
    {
        var i = _items.FindIndex(x => x.Id == merged.Id);
        if (i < 0) return null;
        _items[i] = merged;
        if (additionalDedupKey is not null) _keys.TryAdd(additionalDedupKey, merged.Id);
        return merged;
    }

    public bool HasDedupKey(string key) => _keys.ContainsKey(key);

    public int Delete(IEnumerable<long> ids)
    {
        var set = ids.ToHashSet();
        foreach (var k in _keys.Where(kv => set.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _keys.Remove(k);
        return _items.RemoveAll(x => set.Contains(x.Id));
    }

    public int Clear() { var n = _items.Count; _items.Clear(); _keys.Clear(); return n; }
}
