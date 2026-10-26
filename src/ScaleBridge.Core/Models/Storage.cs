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

    /// <summary>按显示时间倒序（新的在前）返回全部记录。</summary>
    IReadOnlyList<Measurement> List();

    /// <summary>删除指定主键的记录，返回实际删除的条数。</summary>
    int Delete(IEnumerable<long> ids);

    /// <summary>清空全部，返回删除的条数。</summary>
    int Clear();
}
