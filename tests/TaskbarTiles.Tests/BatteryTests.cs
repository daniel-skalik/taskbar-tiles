using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Battery;

namespace TaskbarTiles.Tests;

public class BatteryViewTests
{
    static DeviceBattery Device(string name, int? percent, BatteryLevel? level = null, bool charging = false, bool asleep = false) =>
        new(name, name, DeviceType.Other, percent, level, charging, asleep);

    [Fact]
    public void Tile_shows_the_lowest_device_and_lists_all_in_the_tooltip()
    {
        var tile = BatteryView.Tile([Device("Mouse", 75), Device("Keys", 45), Device("Buds", 100)]);

        Assert.Equal("45 %", tile.Title);
        Assert.Equal("Keys", tile.Subtitle);
        Assert.Equal(new TileIcon("battery-5"), tile.Icon);
        Assert.False(tile.Urgent);
        Assert.Equal("Keys: 45 %\nMouse: 75 %\nBuds: 100 %", tile.Tooltip);
    }

    [Theory]
    [InlineData(9, TileColor.Bad)]
    [InlineData(19, TileColor.Warn)]
    [InlineData(20, TileColor.Neutral)] // the tile stays calm when nothing is low
    public void Tile_color_follows_the_level(int percent, TileColor color) =>
        Assert.Equal(color, BatteryView.Tile([Device("Mouse", percent)]).Icon!.Color);

    [Fact]
    public void Tile_is_urgent_below_five_percent_unless_charging()
    {
        Assert.True(BatteryView.Tile([Device("Mouse", 4)]).Urgent);
        Assert.False(BatteryView.Tile([Device("Mouse", 4, charging: true)]).Urgent);
    }

    [Theory]
    [InlineData(0, false, "battery-0")]
    [InlineData(44, false, "battery-4")]
    [InlineData(45, false, "battery-5")]
    [InlineData(100, false, "battery-10")]
    [InlineData(62, true, "battery-charging-6")]
    public void Glyph_is_tenths_of_full(int percent, bool charging, string glyph) =>
        Assert.Equal(glyph, BatteryView.Glyph(Device("Mouse", percent, charging: charging)));

    [Fact]
    public void A_level_without_percent_sorts_and_shows_as_the_level()
    {
        var devices = new[] { Device("Keys", 30), Device("Mouse", null, BatteryLevel.Low), Device("Odd", null) };

        Assert.Equal(new[] { "Mouse", "Keys", "Odd" }, BatteryView.Sorted(devices).Select(d => d.Name));
        Assert.Equal("Low", BatteryView.Tile(devices).Title);
        Assert.Equal(TileColor.Warn, BatteryView.Tile(devices).Icon!.Color);
    }

    [Fact]
    public void No_devices_says_so()
    {
        var tile = BatteryView.Tile([]);

        Assert.Equal("No devices", tile.Subtitle);
        Assert.Equal("battery-0", tile.Icon!.Glyph);
        Assert.Equal("No devices", BatteryView.Flyout([]).Subtitle);
    }

    [Fact]
    public void Popup_lists_every_device_with_its_state()
    {
        var flyout = BatteryView.Flyout([Device("Mouse", 75, charging: true), Device("Keys", 45, asleep: true), Device("Pad", null, BatteryLevel.Good)]);

        Assert.Equal("3 devices", flyout.Subtitle);
        Assert.Equal(new[] { "Keys", "Pad", "Mouse" }, flyout.Rows.Select(r => r.Text));
        Assert.Equal(new[] { "45 %, asleep", "good", "75 %, charging" }, flyout.Rows.Select(r => r.Value));
        Assert.Equal(TileColor.Ok, flyout.Rows[0].Icon!.Color);
        Assert.Equal(BatteryView.BluetoothSettingsAction, Assert.Single(flyout.Actions).Id);
    }
}

public class BatteryConnectorTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("{}", 60)]
    [InlineData("""{ "interval": "30s" }""", 30)]
    [InlineData("""{ "interval": "1s" }""", 10)]
    [InlineData("""{ "interval": "5h" }""", 3600)]
    [InlineData("""{ "interval": 30 }""", 60)]
    public void Interval_comes_from_settings_clamped(string settings, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BatteryConnector.Interval(Json(settings), new CollectingLog()));

    [Fact]
    public void A_device_seen_by_two_sources_appears_once_with_the_later_reading()
    {
        var merged = BatteryConnector.Merge([
            [new DeviceBattery("bt", "MX Master 3S", DeviceType.Mouse, 70)],
            [new DeviceBattery("logi", "MX Master 3S", DeviceType.Mouse, 75, Charging: true), new DeviceBattery("k", "MX Keys S", DeviceType.Keyboard, 45)],
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("logi", merged.Single(d => d.Name == "MX Master 3S").Id);
    }

    [Fact]
    public void An_awake_reading_beats_an_asleep_one_and_one_sources_twins_both_stay()
    {
        var merged = BatteryConnector.Merge([
            [new DeviceBattery("bt", "MX Master 3S", DeviceType.Mouse, 70), new DeviceBattery("h1", "Buds", DeviceType.Headphones, 80),
             new DeviceBattery("h2", "Buds", DeviceType.Headphones, 30)],
            [new DeviceBattery("logi", "MX Master 3S", DeviceType.Mouse, 75, Asleep: true)],
        ]);

        Assert.Equal("bt", merged.Single(d => d.Name == "MX Master 3S").Id);
        Assert.Equal(2, merged.Count(d => d.Name == "Buds"));
    }

    [Fact]
    public async Task Disposing_twice_or_before_start_finishes_is_safe()
    {
        var release = new TaskCompletionSource();
        var connector = new BatteryConnector([new Source("Slow", () => { release.Task.Wait(); return []; })]);
        var ctx = new Context();
        var start = Task.Run(() => connector.StartAsync(ctx, CancellationToken.None));
        await Task.Delay(50);

        var dispose = connector.DisposeAsync().AsTask();
        release.SetResult();
        await start;
        await dispose;
        await connector.DisposeAsync();

        Assert.True(start.IsCompletedSuccessfully);
    }

    sealed class Source(string name, Func<IReadOnlyList<DeviceBattery>> read) : IBatterySource
    {
        public string Name => name;
        public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct) => Task.FromResult(read());
    }

    sealed class Context : ITileContext
    {
        public string TileId => "battery";
        public JsonElement Settings { get; init; } = Json("{}");
        public string ConfigDirectory => "";
        public List<TileState> Updates { get; } = [];
        public List<string> Errors { get; } = [];
        public void Update(TileState state) => Updates.Add(state);
        public void ReportError(string message, string? details = null) => Errors.Add($"{message}|{details}");
        public event EventHandler? Resumed { add { } remove { } }
        public ITileLog Log { get; } = new CollectingLog();
    }

    [Fact]
    public async Task One_failing_source_is_logged_and_the_others_still_show()
    {
        var connector = new BatteryConnector([
            new Source("Bluetooth", () => throw new InvalidOperationException("radio off")),
            new Source("Logitech", () => [new DeviceBattery("k", "MX Keys S", DeviceType.Keyboard, 45)]),
        ]);
        var ctx = new Context();

        await connector.StartAsync(ctx, CancellationToken.None);

        Assert.Equal("45 %", Assert.Single(ctx.Updates).Title);
        Assert.Empty(ctx.Errors);
        Assert.Contains(((CollectingLog)ctx.Log).Snapshot(), m => m.Contains("radio off"));
        Assert.Equal("MX Keys S", Assert.Single((await connector.GetFlyoutAsync(CancellationToken.None))!.Rows).Text);
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task All_sources_failing_shows_the_error()
    {
        var connector = new BatteryConnector([new Source("Bluetooth", () => throw new InvalidOperationException("radio off"))]);
        var ctx = new Context();

        await connector.StartAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.Updates);
        Assert.Equal("Could not read batteries|Bluetooth: radio off", Assert.Single(ctx.Errors));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task Refresh_reads_again()
    {
        var percent = 50;
        var connector = new BatteryConnector([new Source("Logitech", () => [new DeviceBattery("m", "Mouse", DeviceType.Mouse, percent)])]);
        var ctx = new Context();
        await connector.StartAsync(ctx, CancellationToken.None);

        percent = 40;
        await connector.OnActionAsync(TileActions.Refresh, CancellationToken.None);

        Assert.Equal(new[] { "50 %", "40 %" }, ctx.Updates.Select(u => u.Title));
        await connector.DisposeAsync();
    }

    [Theory]
    [InlineData("Communication.Headset.Bluetooth,Audio", DeviceType.Headphones)]
    [InlineData("Input.Mouse", DeviceType.Mouse)]
    [InlineData("Input.Keyboard", DeviceType.Keyboard)]
    [InlineData("Phone", DeviceType.Other)]
    public void Bluetooth_type_comes_from_category_ids(string ids, DeviceType type) =>
        Assert.Equal(type, BluetoothSource.TypeFrom(ids.Split(',')));
    [Fact]
    public async Task Show_hides_devices_not_listed_but_offers_every_device_seen()
    {
        var connector = new BatteryConnector([new Source("Logitech", () => [
            new DeviceBattery("m", "Mouse", DeviceType.Mouse, 80), new DeviceBattery("k", "MX Keys", DeviceType.Keyboard, 20)])]);
        var ctx = new Context { Settings = Json("""{ "show": ["mouse"] }""") };

        await connector.StartAsync(ctx, CancellationToken.None);

        Assert.Equal("80 %", Assert.Single(ctx.Updates).Title);
        Assert.Equal("Mouse", Assert.Single((await connector.GetFlyoutAsync(CancellationToken.None))!.Rows).Text);
        Assert.Equal(new[] { "Mouse", "MX Keys" }, await connector.GetChoicesAsync(BatteryConnector.ShowKey, CancellationToken.None));
        Assert.Empty(await connector.GetChoicesAsync("interval", CancellationToken.None));
        await connector.DisposeAsync();
    }

    [Fact]
    public void Without_a_valid_show_every_device_shows()
    {
        var log = new CollectingLog();

        Assert.Null(BatteryConnector.Shown(Json("{}"), log));
        Assert.Empty(log.Snapshot());
        Assert.Null(BatteryConnector.Shown(Json("""{ "show": "Mouse" }"""), log));
        Assert.Contains(log.Snapshot(), m => m.Contains("\"show\""));
        Assert.Empty(BatteryConnector.Filter([new DeviceBattery("m", "Mouse", DeviceType.Mouse, 1)], new HashSet<string>()));
    }
}

public class HidppTests
{
    /// <summary>Answers requests from a table keyed by (device, feature, function, first argument); everything else is an error.</summary>
    sealed class FakeTransport(string path = "receiver") : IHidppTransport
    {
        public Dictionary<(byte Device, byte Feature, byte Function, int Arg), byte[]> Answers { get; } = [];
        public string Path => path;
        public bool Disposed { get; private set; }
        public bool IsDead { get; set; }
        public int Requests { get; private set; }

        public Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[] args, CancellationToken ct)
        {
            Requests++;
            var arg = function == 0 && feature == 0 ? args[0] << 8 | args[1] : args.Length > 0 ? args[0] : -1;
            if (!Answers.TryGetValue((device, feature, function, arg), out var answer)
                && !Answers.TryGetValue((device, feature, function, -1), out answer)) return Task.FromResult<byte[]?>(null);
            var padded = new byte[16];
            answer.CopyTo(padded, 0);
            return Task.FromResult<byte[]?>(padded);
        }

        public void Dispose() => Disposed = true;

        /// <summary>A HID++ 4.5 device at <paramref name="index"/> with DeviceName at 3 and the battery feature at 8.</summary>
        public FakeTransport WithDevice(byte index, string name, byte type, ushort battery = 0x1004, byte percentFlag = 0x02)
        {
            Answers[(index, 0, 1, -1)] = [4, 5, 0x5A];
            Answers[(index, 0, 0, 0x0005)] = [3];
            Answers[(index, 0, 0, battery)] = [8];
            Answers[(index, 3, 0, -1)] = [(byte)name.Length];
            for (var offset = 0; offset < name.Length; offset += 16)
                Answers[(index, 3, 1, offset)] = System.Text.Encoding.ASCII.GetBytes(name[offset..Math.Min(name.Length, offset + 16)]);
            Answers[(index, 3, 2, -1)] = [type];
            if (battery == 0x1004) Answers[(index, 8, 0, -1)] = [0x0F, percentFlag];
            return this;
        }

        public void Status(byte index, params byte[] status) => Answers[(index, 8, BatteryFunction(index), -1)] = status;

        /// <summary>Unified Battery reads status with function 1, Battery Level Status with function 0.</summary>
        byte BatteryFunction(byte index) => Answers.ContainsKey((index, 8, 1, -1)) || Answers.ContainsKey((index, 0, 0, 0x1004)) ? (byte)1 : (byte)0;
    }

    static async Task<IReadOnlyList<DeviceBattery>> Read(LogitechSource source) => await source.ReadAsync(CancellationToken.None);

    /// <summary>A source over fixed transports; each opens once.</summary>
    static LogitechSource Logi(params FakeTransport[] transports) => Logi(() => transports);

    /// <summary>A source whose scan finds the transports <paramref name="present"/> returns at that moment.</summary>
    static LogitechSource Logi(Func<IReadOnlyList<FakeTransport>> present) => new(
        _ => Task.FromResult<IReadOnlyList<HidppPort>>(present().Select(t => new HidppPort(t.Path)).ToList()),
        port => present().First(t => t.Path == port.Path));

    [Fact]
    public async Task A_receiver_lists_its_paired_devices()
    {
        var receiver = new FakeTransport().WithDevice(1, "MX Keys S", 0).WithDevice(2, "MX Master 3S", 3);
        receiver.Status(1, 45, 4, 0, 0);
        receiver.Status(2, 75, 8, 0, 0);

        var devices = await Read(Logi(receiver));

        Assert.Equal(new[] { ("MX Keys S", DeviceType.Keyboard, 45), ("MX Master 3S", DeviceType.Mouse, 75) },
            devices.Select(d => (d.Name, d.Type, d.Percent ?? -1)));
        Assert.All(devices, d => Assert.False(d.Charging));
    }

    [Fact]
    public async Task A_directly_connected_device_answers_at_0xFF()
    {
        var device = new FakeTransport("direct").WithDevice(LogitechSource.DirectDevice, "G Pro", 3);
        device.Status(LogitechSource.DirectDevice, 60, 4, 1, 1);

        var reading = Assert.Single(await Read(Logi(device)));

        Assert.Equal("G Pro", reading.Name);
        Assert.True(reading.Charging);
    }

    [Fact]
    public async Task A_long_name_is_read_in_chunks()
    {
        var receiver = new FakeTransport().WithDevice(1, "Logitech Wireless Mouse M720", 3);
        receiver.Status(1, 10, 2, 0, 0);

        Assert.Equal("Logitech Wireless Mouse M720", Assert.Single(await Read(Logi(receiver))).Name);
    }

    [Fact]
    public async Task Without_percentage_support_the_level_is_used()
    {
        var receiver = new FakeTransport().WithDevice(1, "K400", 0, percentFlag: 0);
        receiver.Status(1, 0, 2, 0, 0);

        var reading = Assert.Single(await Read(Logi(receiver)));

        Assert.Null(reading.Percent);
        Assert.Equal(BatteryLevel.Low, reading.Level);
    }

    [Fact]
    public async Task Older_devices_use_battery_level_status()
    {
        var receiver = new FakeTransport().WithDevice(1, "M705", 3, battery: 0x1000);
        receiver.Status(1, 30, 20, 1);

        var reading = Assert.Single(await Read(Logi(receiver)));

        Assert.Equal(30, reading.Percent);
        Assert.True(reading.Charging);
    }

    [Fact]
    public async Task A_device_that_stops_answering_keeps_its_last_reading_as_asleep()
    {
        var receiver = new FakeTransport().WithDevice(1, "MX Keys S", 0);
        receiver.Status(1, 45, 4, 0, 0);
        var source = Logi(receiver);
        await Read(source);

        receiver.Answers.Clear(); // asleep: every request now fails

        var reading = Assert.Single(await Read(source));
        Assert.Equal(45, reading.Percent);
        Assert.True(reading.Asleep);
    }

    [Fact]
    public async Task A_device_never_read_and_slots_without_battery_are_skipped()
    {
        var receiver = new FakeTransport();
        receiver.Answers[(1, 0, 1, -1)] = [4, 5, 0x5A]; // answers, but has no battery feature

        Assert.Empty(await Read(Logi(receiver)));
    }

    [Fact]
    public async Task Names_and_features_are_learned_once()
    {
        var receiver = new FakeTransport().WithDevice(1, "MX Keys S", 0);
        receiver.Status(1, 45, 4, 0, 0);
        var source = Logi(receiver);
        await Read(source);
        var first = receiver.Requests;

        await Read(source);

        Assert.True(receiver.Requests - first < first, $"second read took {receiver.Requests - first} requests, first {first}");
    }

    [Fact]
    public async Task An_open_receiver_is_kept_a_dead_one_reopened_and_an_unplugged_one_closed()
    {
        var receiver = new FakeTransport().WithDevice(1, "MX Keys S", 0);
        receiver.Status(1, 45, 4, 0, 0);
        var replugged = new FakeTransport().WithDevice(1, "MX Keys S", 0);
        replugged.Status(1, 40, 4, 0, 0);
        IReadOnlyList<FakeTransport> present = [receiver];
        var opened = 0;
        var source = new LogitechSource(
            _ => Task.FromResult<IReadOnlyList<HidppPort>>(present.Select(t => new HidppPort(t.Path)).ToList()),
            port => { opened++; return present.First(t => t.Path == port.Path); });

        await Read(source);
        await Read(source);
        Assert.Equal(1, opened); // still open: not opened again

        receiver.IsDead = true; // unplugged and plugged back in between two reads: same path
        present = [replugged];
        var reading = Assert.Single(await Read(source));
        Assert.True(receiver.Disposed);
        Assert.Equal(40, reading.Percent);
        Assert.False(reading.Asleep);

        present = [];
        Assert.Empty(await Read(source));
        Assert.True(replugged.Disposed);
    }

    [Fact]
    public async Task A_failing_receiver_does_not_hide_another_ones_devices()
    {
        var broken = new ThrowingTransport();
        var receiver = new FakeTransport("second").WithDevice(1, "MX Keys S", 0);
        receiver.Status(1, 45, 4, 0, 0);
        var source = new LogitechSource(
            _ => Task.FromResult<IReadOnlyList<HidppPort>>([new HidppPort(broken.Path), new HidppPort(receiver.Path)]),
            port => port.Path == broken.Path ? broken : receiver);

        Assert.Equal("MX Keys S", Assert.Single(await Read(source)).Name);
    }

    sealed class ThrowingTransport : IHidppTransport
    {
        public string Path => "broken";
        public bool IsDead => false;
        public Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[] args, CancellationToken ct) =>
            throw new InvalidOperationException("driver hiccup");
        public void Dispose() { }
    }
}

public class HidTransportTests
{
    [Fact]
    public void Both_collections_of_one_interface_share_a_key() =>
        Assert.Equal(
            HidTransport.InterfaceKey(@"\\?\HID#VID_046D&PID_C548&MI_02&Col01#9&1584edf3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
            HidTransport.InterfaceKey(@"\\?\HID#VID_046D&PID_C548&MI_02&Col02#9&1584edf3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));

    static byte[] Long(params byte[] head) { var r = new byte[20]; head.CopyTo(r, 0); return r; }

    [Fact]
    public void Responses_are_matched_to_their_request()
    {
        var request = Long(0x11, 1, 8, 0x1A);

        Assert.Equal(new byte[] { 45, 4 }, HidTransport.Match(request, Long(0x11, 1, 8, 0x1A, 45, 4))![..2]);
        Assert.Null(HidTransport.Match(request, Long(0x11, 2, 8, 0x1A, 45)));       // another device
        Assert.Null(HidTransport.Match(request, Long(0x11, 1, 8, 0x0A, 45)));       // another function
        Assert.Empty(HidTransport.Match(request, Long(0x11, 1, 0xFF, 8, 0x1A, 5))!); // HID++ 2.0 error
        Assert.Empty(HidTransport.Match(request, [0x10, 1, 0x8F, 8, 0x1A, 9, 0])!);  // HID++ 1.0 error on the short collection
        Assert.Null(HidTransport.Match(request, [0x10, 1, 0x41, 4, 0, 0, 0]));       // a connection notification
        Assert.Null(HidTransport.Match(request, [0x10, 1, 0x8F, 8, 0x1F, 9, 0]));    // an error for another program's request
        Assert.Null(HidTransport.Match(request, Long(0x11, 1, 8, 0x19, 45)));       // a late answer to an earlier request
    }

    [Fact]
    public void Each_request_gets_the_next_software_id_and_never_zero()
    {
        Assert.Equal(1, HidTransport.NextSoftwareId(0));
        Assert.Equal(2, HidTransport.NextSoftwareId(1));
        Assert.Equal(1, HidTransport.NextSoftwareId(15));
        Assert.DoesNotContain(Enumerable.Range(0, 16), i => HidTransport.NextSoftwareId((byte)i) == 0);
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_046D&PID_C548&MI_02&Col02#9&1584edf3&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
    [InlineData(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b034_REV&0007#a&1&0#{4d1e55b2}", true)]
    [InlineData(@"\\?\HID#VID_1532&PID_0084&MI_02#8&1#{4d1e55b2}", false)]
    public void Logitech_paths_are_recognized_over_usb_and_bluetooth(string path, bool logitech) =>
        Assert.Equal(logitech, HidTransport.IsLogitech(path));

}
