using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScaleBridge.App.Localization;
using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;
using ScaleBridge.Core.Protocol;

namespace ScaleBridge.App.ViewModels;

/// <summary>显示用格式：只在显示时舍入（kg 3 位 / lb 2 位，AwayFromZero），存储与导出永远是精确值（NOTES Q1）。</summary>
public static class DisplayFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Weight(decimal? kg, WeightUnit unit) => kg is null ? "—" : unit == WeightUnit.Kg
        ? decimal.Round(kg.Value, 3, MidpointRounding.AwayFromZero).ToString("0.000", Inv)
        : decimal.Round(UnitConversion.KilogramsToPounds(kg.Value), 2, MidpointRounding.AwayFromZero).ToString("0.00", Inv);

    public static string UnitName(WeightUnit unit) => unit == WeightUnit.Kg ? "kg" : "lb";

    public static string One(decimal? v, string suffix = "") => v is null ? "—" : decimal.Round(v.Value, 1, MidpointRounding.AwayFromZero).ToString("0.0", Inv) + suffix;

    public static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Inv);
}

/// <summary>下拉项：文字来自 Loc 键（语言切换后 <see cref="Refresh"/>）或固定文字。</summary>
public sealed partial class Choice<T> : ObservableObject
{
    private readonly Loc _loc;
    private readonly Func<Loc, string> _text;

    public Choice(T value, Loc loc, Func<Loc, string> text)
    {
        Value = value;
        _loc = loc;
        _text = text;
    }

    public T Value { get; }
    public string Text => _text(_loc);
    public void Refresh() => OnPropertyChanged(nameof(Text));
    public override string ToString() => Text;
}

public sealed partial class DeviceItem : ObservableObject
{
    private readonly Loc _loc;

    public DeviceItem(AdvertisedDevice ad, Loc loc)
    {
        _loc = loc;
        Id = ad.Id;
        Update(ad);
    }

    public string Id { get; }
    public string? Name { get; private set; }
    public int? Rssi { get; private set; }
    public ScaleProtocols Protocols { get; private set; }
    public bool IsDemo { get; private set; }
    public DateTimeOffset SeenAt { get; private set; }

    public string NameText => string.IsNullOrWhiteSpace(Name) ? _loc["Device.Unnamed"] : Name!;
    public string RssiText => Rssi is { } r ? r.ToString(CultureInfo.InvariantCulture) + " dBm" : "—";
    public string ProtocolText => Protocols == ScaleProtocols.None ? _loc["Device.ProtocolUnknown"]
        : string.Join(" + ", new[]
        {
            Protocols.HasFlag(ScaleProtocols.WeightScale) ? "Weight Scale 0x181D" : null,
            Protocols.HasFlag(ScaleProtocols.BodyComposition) ? "Body Composition 0x181B" : null,
        }.Where(s => s is not null));
    /// <summary>所有协议目前都未真机验证 → 一律「实验性」；Fake 设备另标「演示」。</summary>
    public string TagText => IsDemo ? _loc["Tag.Demo"] + " · " + _loc["Tag.Experimental"] : _loc["Tag.Experimental"];

    public void Update(AdvertisedDevice ad)
    {
        Name = string.IsNullOrWhiteSpace(ad.Name) ? Name : ad.Name;
        Rssi = ad.Rssi;
        Protocols = DeviceClassifier.Classify(ad);
        IsDemo = ad.IsDemo;
        SeenAt = ad.SeenAt;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(RssiText));
        OnPropertyChanged(nameof(ProtocolText));
        OnPropertyChanged(nameof(TagText));
    }
}

public sealed partial class HistoryItem : ObservableObject
{
    private readonly Loc _loc;
    private WeightUnit _unit;

    [ObservableProperty] private bool _isChecked;

    public HistoryItem(Measurement m, WeightUnit unit, Loc loc)
    {
        Measurement = m;
        _unit = unit;
        _loc = loc;
    }

    public Measurement Measurement { get; }
    public long Id => Measurement.Id;
    public string TimeText => DisplayFormat.Time(Measurement.DisplayTime);
    public string WeightText => DisplayFormat.Weight(Measurement.WeightKg, _unit) + " " + DisplayFormat.UnitName(_unit);
    public string BmiText => DisplayFormat.One(Measurement.Bmi);
    public string FatText => DisplayFormat.One(Measurement.BodyFatPercent, " %");
    public string DeviceText => string.IsNullOrWhiteSpace(Measurement.DeviceName) ? Measurement.DeviceId : Measurement.DeviceName!;
    public string SourceText => Measurement.Source == MeasurementSource.WeightScale ? "0x2A9D" : "0x2A9C";

    public string FlagsText
    {
        get
        {
            var parts = new List<string>();
            if (Measurement.IsDemo) parts.Add(_loc["Tag.Demo"]);
            if (Measurement.IsExperimental) parts.Add(_loc["Tag.Experimental"]);
            if (MeasurementPolicy.IsAbnormal(MeasurementPolicy.ClassifyWeight(Measurement.WeightKg))) parts.Add(_loc["Tag.Abnormal"]);
            if (MeasurementPolicy.IsDeviceClockSuspect(Measurement)) parts.Add(_loc["Tag.ClockSuspect"]);
            return string.Join(" · ", parts);
        }
    }

    public string SearchText =>
        string.Join(' ', TimeText, WeightText, BmiText, FatText, DeviceText, Measurement.DeviceId, SourceText, FlagsText);

    public void Refresh(WeightUnit unit)
    {
        _unit = unit;
        OnPropertyChanged(nameof(WeightText));
        OnPropertyChanged(nameof(FlagsText));
    }
}
