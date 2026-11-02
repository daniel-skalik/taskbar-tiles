using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.SystemMonitor;

namespace TaskbarTiles.Tests;

public class SystemMonitorTests
{
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    const long Gib = 1024L * 1024 * 1024;

    static SystemReading Reading(double cpu = 12, double ramPercent = 48, double? gpu = 5, double down = 1.2 * 1024 * 1024,
        double up = 84 * 1024, params DiskReading[] disks)
    {
        disks = disks.Length > 0 ? disks : [new DiskReading("C:", 476 * Gib, 138 * Gib), new DiskReading("D:", 1000 * Gib, 900 * Gib)];
        var total = 16 * Gib;
        return new SystemReading(cpu, new MemoryReading((long)(total * ramPercent / 100), total), gpu, down, up, disks, 16);
    }

    static readonly Metric[] All = [Metric.Cpu, Metric.Ram, Metric.Gpu, Metric.Network, Metric.Disk];

    // --- settings -------------------------------------------------------------------------------------------------

    [Fact]
    public void Show_absent_means_all_five()
    {
        var log = new CollectingLog();
        Assert.Equal(All, SystemView.Shown(Json("{}"), log));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Show_subset_keeps_the_fixed_order_and_ignores_case_and_duplicates()
    {
        var shown = SystemView.Shown(Json("""{"show":["disk","cpu","Cpu","GPU"]}"""), new CollectingLog());
        Assert.Equal([Metric.Cpu, Metric.Gpu, Metric.Disk], shown);
    }

    [Theory]
    [InlineData("""{"show":"CPU"}""")]
    [InlineData("""{"show":[1,2]}""")]
    [InlineData("""{"show":["Toaster"]}""")]
    [InlineData("""{"show":null}""")]
    public void Show_bad_falls_back_to_all_with_a_warning(string json)
    {
        var log = new CollectingLog();
        Assert.Equal(All, SystemView.Shown(Json(json), log));
        Assert.Contains(log.Snapshot(), m => m.StartsWith("WARN"));
    }

    [Fact]
    public void Show_skips_unknown_entries_but_keeps_the_known_ones()
    {
        var log = new CollectingLog();
        Assert.Equal([Metric.Ram], SystemView.Shown(Json("""{"show":["RAM","Toaster"]}"""), log));
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Show_empty_list_shows_all()
    {
        Assert.Equal(All, SystemView.Shown(Json("""{"show":[]}"""), new CollectingLog()));
    }

    [Theory]
    [InlineData("{}", false, 0)]
    [InlineData("""{"layout":"All at once"}""", false, 0)]
    [InlineData("""{"layout":"Rotate"}""", true, 0)]
    [InlineData("""{"layout":"rotate"}""", true, 0)]
    [InlineData("""{"layout":"Spin"}""", false, 1)]
    [InlineData("""{"layout":3}""", false, 1)]
    public void Layout_is_parsed(string json, bool rotating, int warnings)
    {
        var log = new CollectingLog();
        Assert.Equal(rotating, SystemView.IsRotating(Json(json), log));
        Assert.Equal(warnings, log.Snapshot().Length);
    }

    [Theory]
    [InlineData("{}", 5, 0)]
    [InlineData("""{"rotate":"8s"}""", 8, 0)]
    [InlineData("""{"rotate":"1m"}""", 60, 0)]
    [InlineData("""{"rotate":"500ms"}""", 2, 1)] // clamped up
    [InlineData("""{"rotate":"2h"}""", 60, 1)]   // clamped down
    [InlineData("""{"rotate":"soon"}""", 5, 1)]
    [InlineData("""{"rotate":7}""", 5, 1)]
    public void Rotate_is_parsed_and_clamped(string json, int seconds, int warnings)
    {
        var log = new CollectingLog();
        Assert.Equal(TimeSpan.FromSeconds(seconds), SystemView.RotateInterval(Json(json), log));
        Assert.Equal(warnings, log.Snapshot().Length);
    }

    // --- formatting -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(84 * 1024, "84 KB/s")]
    [InlineData(1.5 * 1024, "1.5 KB/s")]
    [InlineData(1.2 * 1024 * 1024, "1.2 MB/s")]
    [InlineData(12.4 * 1024 * 1024, "12 MB/s")]
    [InlineData(1023.9 * 1024, "1.0 MB/s")] // would round to 1024 KB/s
    public void Rates_pick_a_unit(double bytes, string text) => Assert.Equal(text, SystemView.Rate(bytes));

    [Theory]
    [InlineData(476, "476 GB")]
    [InlineData(7.7, "7.7 GB")]
    [InlineData(1843, "1.8 TB")]
    public void Sizes_use_gb_or_tb(double gb, string text) => Assert.Equal(text, SystemView.Size((long)(gb * Gib)));

    [Fact]
    public void Gpu_percent_sums_engines_per_adapter_and_takes_the_busiest()
    {
        var a = "pid_1_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D";
        var a2 = "pid_2_luid_0x00000000_0x0000A1B2_phys_0_eng_1_engtype_3D";
        var b = "pid_3_luid_0x00000000_0x0000C3D4_phys_0_eng_0_engtype_3D";
        Assert.Equal(40, WindowsSampler.GpuPercent([(a, 10), (a2, 30), (b, 25)]));
        Assert.Equal(100, WindowsSampler.GpuPercent([(a, 80), (a2, 70)]));
        Assert.Equal(0, WindowsSampler.GpuPercent([]));
    }

    // --- CPU ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cpu_percent_is_busy_share_of_the_interval()
    {
        // Kernel time includes idle: 100 ticks passed, 70 of them idle -> 30% busy.
        Assert.Equal(30, WindowsSampler.CpuPercent((1000, 2000, 500), (1070, 2060, 540)), 6);
    }

    [Fact]
    public void Cpu_percent_is_zero_when_no_time_passed_and_clamped()
    {
        Assert.Equal(0, WindowsSampler.CpuPercent((10, 20, 30), (10, 20, 30)));
        Assert.Equal(0, WindowsSampler.CpuPercent((10, 20, 30), (50, 25, 30))); // idle > total: counters went odd
        Assert.Equal(100, WindowsSampler.CpuPercent((10, 20, 30), (10, 60, 70)));
    }

    // --- levels ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, TileColor.Neutral)]
    [InlineData(0.0, TileColor.Ok)]
    [InlineData(79.9, TileColor.Ok)]
    [InlineData(80.0, TileColor.Warn)]
    [InlineData(94.9, TileColor.Warn)]
    [InlineData(95.0, TileColor.Bad)]
    [InlineData(100.0, TileColor.Bad)]
    public void Levels_follow_the_thresholds(double? percent, TileColor color) => Assert.Equal(color, SystemView.Level(percent));

    [Fact]
    public void Icon_color_is_the_worst_of_the_shown_metrics_and_neutral_when_calm()
    {
        Assert.Equal(TileColor.Neutral, SystemView.Tile(Reading(), new SystemHistory(), All, false, Metric.Cpu).Icon!.Color);
        Assert.Equal(TileColor.Warn, SystemView.IconColor(Reading(cpu: 85), All));
        Assert.Equal(TileColor.Bad, SystemView.IconColor(Reading(cpu: 85, ramPercent: 97), All));
        // A hot metric that is not shown does not color the icon.
        Assert.Equal(TileColor.Neutral, SystemView.IconColor(Reading(cpu: 99), [Metric.Ram]));
    }

    [Fact]
    public void Network_never_colors_the_icon_and_gpu_na_is_neutral()
    {
        Assert.Equal(TileColor.Neutral, SystemView.IconColor(Reading(down: 1e12, gpu: null), [Metric.Network, Metric.Gpu]));
    }

    // --- title and subtitle ---------------------------------------------------------------------------------------

    [Fact]
    public void All_five_split_two_and_three()
    {
        var tile = SystemView.Tile(Reading(), new SystemHistory(), All, false, Metric.Cpu);
        Assert.Equal("CPU 12% · RAM 48%", tile.Title);
        Assert.Equal("GPU 5% · ↓1.2 MB/s · C: 71%", tile.Subtitle);
        Assert.Null(tile.Sparkline);
        Assert.Null(tile.Progress);
    }

    [Theory]
    [InlineData(1, "A", null)]
    [InlineData(2, "A", "B")]
    [InlineData(3, "A", "B · C")]
    [InlineData(4, "A · B", "C · D")]
    [InlineData(5, "A · B", "C · D · E")]
    public void Split_puts_the_first_half_on_the_title(int count, string title, string? subtitle)
    {
        var (t, s) = SystemView.Split(new[] { "A", "B", "C", "D", "E" }.Take(count).ToList());
        Assert.Equal(title, t);
        Assert.Equal(subtitle, s);
    }

    [Fact]
    public void Disk_shows_the_fullest_drive()
    {
        var r = Reading(disks: [new DiskReading("C:", 100 * Gib, 90 * Gib), new DiskReading("D:", 100 * Gib, 10 * Gib)]);
        Assert.Equal("D: 90%", SystemView.Describe(Metric.Disk, r).Compact);
        Assert.Equal("10 GB free of 100 GB", SystemView.Describe(Metric.Disk, r).Detail);
    }

    [Fact]
    public void A_single_metric_has_its_detail_chart_and_progress()
    {
        var history = new SystemHistory();
        history.Push(Reading(ramPercent: 40));
        history.Push(Reading(ramPercent: 48));

        var tile = SystemView.Tile(Reading(), history, [Metric.Ram], false, Metric.Ram);

        Assert.Equal("RAM 48%", tile.Title);
        Assert.Equal("7.7 of 16 GB", tile.Subtitle);
        Assert.Equal(2, tile.Sparkline!.Count);
        Assert.Equal(0.48, tile.Progress!.Value, 3);
    }

    [Fact]
    public void Gpu_without_counters_reads_na_and_has_no_progress()
    {
        var tile = SystemView.Tile(Reading(gpu: null), new SystemHistory(), [Metric.Gpu], false, Metric.Gpu);
        Assert.Equal("GPU n/a", tile.Title);
        Assert.Null(tile.Progress);
    }

    [Fact]
    public void Tooltip_lists_every_selected_metric_with_details()
    {
        var tile = SystemView.Tile(Reading(), new SystemHistory(), [Metric.Cpu, Metric.Network], false, Metric.Cpu);
        Assert.Equal("CPU: 12% (16 logical cores)\nNetwork: ↓1.2 MB/s (↑84 KB/s)", tile.Tooltip);
    }

    // --- rotate ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Rotate_cycles_through_the_selected_metrics_in_order()
    {
        Metric[] selected = [Metric.Cpu, Metric.Gpu, Metric.Disk];
        var every = TimeSpan.FromSeconds(5);
        var seen = new[] { 0, 4, 5, 9, 10, 14, 15, 20 }
            .Select(s => SystemView.Rotated(selected, TimeSpan.FromSeconds(s), every)).ToArray();
        Assert.Equal([Metric.Cpu, Metric.Cpu, Metric.Gpu, Metric.Gpu, Metric.Disk, Metric.Disk, Metric.Cpu, Metric.Gpu], seen);
    }

    [Fact]
    public void Rotate_with_one_metric_stays_on_it()
    {
        Assert.Equal(Metric.Ram, SystemView.Rotated([Metric.Ram], TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Rotated_tile_shows_the_current_metric_with_name_value_detail_chart_and_progress()
    {
        var history = new SystemHistory();
        history.Push(Reading(cpu: 30));
        history.Push(Reading(cpu: 60));

        var tile = SystemView.Tile(Reading(cpu: 60), history, All, true, Metric.Cpu);

        Assert.Equal("CPU 60%", tile.Title);
        Assert.Equal("16 logical cores", tile.Subtitle);
        Assert.Equal([30.0, 60.0], tile.Sparkline);
        Assert.Equal(0.6, tile.Progress!.Value, 3);
    }

    [Fact]
    public void Rotated_network_charts_the_download_rate_and_has_no_progress()
    {
        var history = new SystemHistory();
        history.Push(Reading(down: 1000));
        history.Push(Reading(down: 2000));

        var tile = SystemView.Tile(Reading(), history, All, true, Metric.Network);

        Assert.Equal("Network ↓1.2 MB/s", tile.Title);
        Assert.Equal("↑84 KB/s", tile.Subtitle);
        Assert.Equal([1000.0, 2000.0], tile.Sparkline);
        Assert.Null(tile.Progress);
    }

    [Fact]
    public void History_keeps_the_last_60_values()
    {
        var history = new SystemHistory();
        for (var i = 0; i < 75; i++) history.Push(Reading(cpu: i));
        var cpu = history.Get(Metric.Cpu);
        Assert.Equal(60, cpu.Count);
        Assert.Equal(15, cpu[0]);
        Assert.Equal(74, cpu[^1]);
    }

    // --- popup ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Popup_lists_every_metric_and_one_row_per_disk_regardless_of_show()
    {
        var flyout = SystemView.Flyout(Reading(cpu: 96, ramPercent: 85), new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal("System", flyout.Title);
        Assert.Equal("Updated 03:04:05", flyout.Subtitle);
        Assert.Equal(["CPU", "RAM", "GPU", "Network", "C:", "D:"], flyout.Rows.Select(r => r.Text).ToArray());
        Assert.Equal("96%", flyout.Rows[0].Value);
        Assert.Equal(TileColor.Bad, flyout.Rows[0].Icon!.Color);
        Assert.Equal("85% · 14 of 16 GB", flyout.Rows[1].Value);
        Assert.Equal(TileColor.Warn, flyout.Rows[1].Icon!.Color);
        Assert.Equal("↓1.2 MB/s ↑84 KB/s", flyout.Rows[3].Value);
        Assert.Equal("138 GB free of 476 GB (71%)", flyout.Rows[4].Value);
        Assert.Equal(TileColor.Ok, flyout.Rows[4].Icon!.Color);
        Assert.Equal(new FlyoutAction(SystemView.TaskManagerAction, "Task Manager"), Assert.Single(flyout.Actions));
    }

    [Fact]
    public void Popup_before_the_first_reading_has_no_rows_but_keeps_the_button()
    {
        var flyout = SystemView.Flyout(null, DateTime.Now);
        Assert.Empty(flyout.Rows);
        Assert.Single(flyout.Actions);
    }

    // --- connector ------------------------------------------------------------------------------------------------

    sealed class FakeSampler : ISystemSampler
    {
        public int Reads, Disposed;
        public SystemReading Read() { Interlocked.Increment(ref Reads); return Reading(cpu: 42); }
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public async Task Connector_updates_the_tile_every_period_and_disposes_the_sampler()
    {
        var sampler = new FakeSampler();
        var ctx = new TestTileContext(settingsJson: """{"show":["CPU"]}""");
        var connector = new SystemConnector(_ => sampler, TimeSpan.FromMilliseconds(40));

        await connector.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates.Count >= 3);
        Assert.Equal("CPU 42%", ctx.Updates[0].Title);
        var popup = await connector.GetFlyoutAsync(CancellationToken.None);
        Assert.Equal(6, popup!.Rows.Count);

        await connector.DisposeAsync();
        var after = ctx.Updates.Count;
        await Task.Delay(150);

        Assert.Equal(1, sampler.Disposed);
        Assert.Equal(after, ctx.Updates.Count);
    }

    [Fact]
    public async Task Connector_survives_a_failing_read()
    {
        var calls = 0;
        var ctx = new TestTileContext();
        var connector = new SystemConnector(_ => new Throwing(() => ++calls == 1), TimeSpan.FromMilliseconds(30));

        await connector.StartAsync(ctx, CancellationToken.None);
        await Eventually.Holds(() => ctx.Updates.Count >= 1);
        await connector.DisposeAsync();

        Assert.Contains(ctx.Logged.Snapshot(), m => m.StartsWith("WARN System readings failed"));
    }

    sealed class Throwing(Func<bool> fail) : ISystemSampler
    {
        public SystemReading Read() => fail() ? throw new InvalidOperationException("boom") : Reading();
        public void Dispose() { }
    }
}
