namespace ScaleBridge.Core.Models;

public enum LanguageSetting
{
    /// <summary>跟随系统语言。</summary>
    System = 0,
    Zh = 1,
    En = 2,
}

/// <summary>用户设置。</summary>
public sealed record AppSettings
{
    public LanguageSetting Language { get; init; } = LanguageSetting.System;
    public WeightUnit Unit { get; init; } = WeightUnit.Kg;
    /// <summary>适配器 id："fake" / "windows" / "linux"。默认 fake（演示）。</summary>
    public string AdapterId { get; init; } = "fake";
    /// <summary>Fake 适配器的演示场景 id。</summary>
    public string ScenarioId { get; init; } = "normal-si";
    public bool ShowBmi { get; init; } = true;
    public bool ShowBodyFat { get; init; } = true;

    public const int DefaultHistoryWindow = 5000;
    public const int MinHistoryWindow = 10;
    public const int MaxHistoryWindow = 100_000;

    /// <summary>
    /// 历史页一次加载（以及每次「加载更多」追加）的最大条数。<b>只限制界面内存窗口，不删除任何数据</b>：
    /// 数据库保留全部记录，导出始终包含全部记录。写入设置库的 history_window 键；越界/非法值回落到合法范围。
    /// </summary>
    public int HistoryWindow { get; init; } = DefaultHistoryWindow;

    public static int NormalizeHistoryWindow(int value) => Math.Clamp(value, MinHistoryWindow, MaxHistoryWindow);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>历史记录存储。实现在 ScaleBridge.Data（SQLite）。</summary>
public interface IMeasurementRepository
{
    /// <summary>保存并返回带主键的记录。</summary>
    Measurement Add(Measurement measurement);

    /// <summary>按显示时间倒序（新的在前）返回全部记录。大历史下代价与总条数成正比：界面不要用它，用 <see cref="ListPage"/>；导出才用它。</summary>
    IReadOnlyList<Measurement> List();

    /// <summary>总条数（不加载记录本身）。</summary>
    int Count();

    /// <summary>
    /// 按显示时间倒序的一页：<paramref name="after"/> 为 null 时从最新开始，否则返回排在该记录「之后」（更旧）的最多
    /// <paramref name="limit"/> 条。用「游标」而不是偏移量，所以翻页过程中又收到新记录也不会错位或重复。
    /// </summary>
    IReadOnlyList<Measurement> ListPage(int limit, Measurement? after = null);

    /// <summary>该去重键（<see cref="MeasurementPolicy.DedupKey"/>）是否已入库。键随记录一起持久化，所以跨重启有效，且不需要把全部历史读进内存。</summary>
    bool HasDedupKey(string key);

    /// <summary>删除指定主键的记录，返回实际删除的条数。</summary>
    int Delete(IEnumerable<long> ids);

    /// <summary>清空全部，返回删除的条数。</summary>
    int Clear();
}
