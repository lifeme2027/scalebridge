using System.Reflection;
using ScaleBridge.App.ViewModels;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Tests;

/// <summary>SB1-M01：历史窗口（最新 N 条 + 加载更多）、新记录增量插入、删除后补满窗口。数据库始终保留全部记录。</summary>
public class HistoryWindowTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0);
    private static readonly MethodInfo OnMeasurement =
        typeof(MainViewModel).GetMethod("OnMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Measurement M(int i, DateTime ts) => new()
    {
        DeviceTimestamp = ts,
        ReceivedAt = new DateTimeOffset(ts, TimeSpan.FromHours(8)),
        WeightKg = 60m + i * 0.005m,
        OriginalWeight = 60m + i * 0.005m,
        OriginalUnit = UnitSystem.Si,
        RawWeight = 12000 + i,
        DeviceId = "DE:MO:00:00:00:01",
        Source = MeasurementSource.WeightScale,
    };

    private static void Receive(MainViewModel vm, Measurement m) => OnMeasurement.Invoke(vm, new object[] { m });

    private static TestApp Seeded(int rows, int window, out MainViewModel vm)
    {
        var t = new TestApp();
        for (var i = 0; i < rows; i++) t.Repo.Add(M(i, T0.AddMinutes(i)));
        vm = t.CreateVm(window);
        return t;
    }

    [Fact]
    public void Only_the_newest_window_is_loaded_and_load_more_pages_back_without_gaps_or_duplicates()
    {
        using var t = Seeded(25, 10, out var vm);
        using var _ = vm;
        Assert.Equal(10, vm.History.Count);
        Assert.True(vm.HasMoreHistory);
        Assert.True(vm.HasHistory);
        Assert.Contains("of 25", vm.HistorySummary);
        Assert.Contains("newest 10", vm.HistorySummary);
        Assert.Equal(T0.AddMinutes(24), vm.History[0].Measurement.DeviceTimestamp); // 最新在最前

        vm.LoadMoreHistoryCommand.Execute(null);
        Assert.Equal(20, vm.History.Count);
        vm.LoadMoreHistoryCommand.Execute(null);
        Assert.Equal(25, vm.History.Count);
        Assert.False(vm.HasMoreHistory);
        Assert.Equal(25, vm.History.Select(h => h.Id).Distinct().Count());
        Assert.Equal(t.Repo.List().Select(x => x.Id), vm.History.Select(h => h.Id)); // 与仓库完全一致
        Assert.DoesNotContain("are loaded", vm.HistorySummary);
    }

    [Fact]
    public void New_records_are_inserted_incrementally_at_their_time_position()
    {
        using var t = Seeded(25, 10, out var vm);
        using var _ = vm;
        var before = vm.History.ToList();

        Receive(vm, M(100, T0.AddMinutes(100)));   // 最新：插到最前
        Assert.Equal(11, vm.History.Count);
        Assert.Equal(T0.AddMinutes(100), vm.History[0].Measurement.DeviceTimestamp);
        Assert.Same(before[0], vm.History[1]);       // 原有行对象没被重建（增量，不是整表重载）

        Receive(vm, M(101, T0.AddMinutes(20) .AddSeconds(30)));   // 乱序到达的较旧记录：插到窗口内的中间
        Assert.Equal(12, vm.History.Count);
        var times = vm.History.Select(h => h.Measurement.DisplayTime).ToList();
        Assert.Equal(times.OrderByDescending(x => x), times);

        Receive(vm, M(102, T0.AddDays(-1)));       // 比窗口里最旧的还旧，库里还有未加载记录：不进窗口
        Assert.Equal(12, vm.History.Count);
        Assert.Equal(28, t.Repo.Count());          // 但已入库
        Assert.Contains("of 28", vm.HistorySummary);

        while (vm.HasMoreHistory) vm.LoadMoreHistoryCommand.Execute(null);
        Assert.Equal(28, vm.History.Count);
        Assert.Equal(T0.AddDays(-1), vm.History[^1].Measurement.DeviceTimestamp);
        Assert.Equal(t.Repo.List().Select(x => x.Id), vm.History.Select(h => h.Id));
    }

    [Fact]
    public async Task Delete_refills_the_window_with_older_rows_and_clear_empties_everything()
    {
        using var t = Seeded(25, 10, out var vm);
        using var _ = vm;
        foreach (var h in vm.History.Take(3)) h.IsChecked = true;
        await vm.DeleteSelectedAsync();
        Assert.Equal(10, vm.History.Count);                 // 用更旧的补满
        Assert.Equal(22, t.Repo.Count());
        Assert.Contains("of 22", vm.HistorySummary);
        Assert.Equal(t.Repo.ListPage(10).Select(x => x.Id), vm.History.Select(h => h.Id));

        await vm.ClearHistoryAsync();
        Assert.Empty(vm.History);
        Assert.False(vm.HasHistory);
        Assert.False(vm.HasMoreHistory);
        Assert.Equal(0, t.Repo.Count());
    }

    [Fact]
    public void Everything_in_the_window_when_history_is_small_and_filter_applies_to_loaded_rows()
    {
        using var t = Seeded(5, 10, out var vm);
        using var _ = vm;
        Assert.Equal(5, vm.History.Count);
        Assert.False(vm.HasMoreHistory);
        vm.FilterText = "zzz-no-match";
        Assert.Empty(vm.History);
        Receive(vm, M(50, T0.AddMinutes(50)));      // 不匹配过滤条件的新记录：入库、进窗口，但不显示
        Assert.Empty(vm.History);
        vm.FilterText = "";
        Assert.Equal(6, vm.History.Count);
    }
}

/// <summary>SB1-M02：同一次称重的两包合并后，历史列表里仍是一行（原位置更新），并带上「已合并/可能重复」标记。</summary>
public class HistoryMergeTests
{
    private static readonly DateTime Stamp = new(2026, 10, 26, 8, 0, 0);
    private static readonly MethodInfo OnMeasurement =
        typeof(MainViewModel).GetMethod("OnMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Measurement Packet(MeasurementSource s, decimal kg = 70m, double sec = 0) => new()
    {
        Source = s, DeviceId = "DE:MO:00:00:00:01", DeviceTimestamp = Stamp, ReceivedAt = new DateTimeOffset(Stamp, TimeSpan.FromHours(8)).AddSeconds(sec),
        WeightKg = kg, OriginalWeight = kg, OriginalUnit = UnitSystem.Si, RawWeight = (int)(kg * 200),
        BodyFatPercent = s == MeasurementSource.BodyComposition ? 21.5m : null,
    };

    private static void Receive(MainViewModel vm, Measurement m) => OnMeasurement.Invoke(vm, new object[] { m });

    [Fact]
    public void Merged_packets_show_as_one_history_row_keeping_the_checkbox_and_marking_the_source()
    {
        using var t = new TestApp();
        using var vm = t.CreateVm(100);
        Receive(vm, Packet(MeasurementSource.WeightScale));
        Assert.Single(vm.History);
        var id = vm.History[0].Id;
        vm.History[0].IsChecked = true;

        Receive(vm, Packet(MeasurementSource.BodyComposition, sec: 0.2));
        Assert.Single(vm.History);                                  // 没有出现第二行，体重也只算一次
        Assert.Equal(1, t.Repo.Count());
        Assert.Equal(id, vm.History[0].Id);
        Assert.True(vm.History[0].IsChecked);
        Assert.Equal("0x2A9D+0x2A9C", vm.History[0].SourceText);
        Assert.Equal(21.5m, vm.History[0].Measurement.BodyFatPercent);
        Assert.Contains("181D+181B", vm.History[0].FlagsText);

        // 重发其中一包：重复，不新增
        Receive(vm, Packet(MeasurementSource.WeightScale, sec: 20));
        Assert.Single(vm.History);
    }

    [Fact]
    public void Uncertain_pairs_stay_as_two_rows_and_the_later_one_is_marked_as_a_possible_duplicate()
    {
        using var t = new TestApp();
        using var vm = t.CreateVm(100);
        Receive(vm, Packet(MeasurementSource.WeightScale, 70m));
        Receive(vm, Packet(MeasurementSource.BodyComposition, 70.5m, sec: 0.2));   // 同一时间戳、体重不同
        Assert.Equal(2, vm.History.Count);
        var marked = vm.History.Single(h => h.Measurement.PossibleSameAsId is not null);
        Assert.Equal(MeasurementSource.BodyComposition, marked.Measurement.Source);
        Assert.Contains("Possible duplicate", marked.FlagsText, StringComparison.Ordinal);
        Assert.DoesNotContain("181D+181B", marked.FlagsText);
    }
}
