using ScaleBridge.Core.Models;

namespace ScaleBridge.Core.Export;

/// <summary>
/// 「可能重复」标记在导出里的表示（CSV/JSON 共用）。<c>possible_duplicate</c> = 该记录带有 <see cref="Measurement.PossibleSameAsId"/>；
/// <c>possible_same_as</c> = 被指向那条记录的 <c>record_id</c>（即本机数据库主键，只在同一个数据库的导出之间有意义，跨设备/跨库不稳定）。
/// 被指向的记录若不在本次导出里（例如已被用户删除），<c>possible_same_as</c> 留空，但 <c>possible_duplicate</c> 仍为 true（与界面一致）。
/// </summary>
internal static class PossibleSameAs
{
    public static HashSet<long> IdSet(IEnumerable<Measurement> list) => new(list.Select(m => m.Id));

    public static long? Target(Measurement m, HashSet<long> exportedIds) =>
        m.PossibleSameAsId is { } t && exportedIds.Contains(t) ? t : null;
}
