using System.IO;
using System.Text;
using System.Text.Json;
using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Clipboard;

namespace TaskbarTiles.Tests;

public class ClipboardHistoryTests
{
    static readonly DateTime T = new(2026, 1, 1, 14, 3, 0);
    static ClipItem Text(string text) => new(ClipKind.Text, text);

    [Fact]
    public void Newest_is_first_and_the_cap_drops_the_oldest()
    {
        var history = new ClipHistory(3);
        foreach (var s in new[] { "a", "b", "c", "d" }) history.Add(Text(s), T);

        Assert.Equal(new[] { "d", "c", "b" }, history.Snapshot().Select(e => e.Item.Text));
    }

    [Fact]
    public void A_repeated_text_moves_up_with_its_id_and_a_new_time()
    {
        var history = new ClipHistory(5);
        var first = history.Add(Text("a"), T);
        history.Add(Text("b"), T);

        var again = history.Add(Text("a"), T.AddMinutes(5));

        var snapshot = history.Snapshot();
        Assert.Equal(new[] { "a", "b" }, snapshot.Select(e => e.Item.Text));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(T.AddMinutes(5), snapshot[0].Time);
    }

    [Fact]
    public void Images_never_merge()
    {
        var history = new ClipHistory(5);
        var image = new ClipItem(ClipKind.Image, "Image 800×600", "Image 800×600");
        history.Add(image, T);
        history.Add(image, T);

        Assert.Equal(2, history.Snapshot().Count);
    }

    [Fact]
    public void Privacy_formats_exclude_a_copy()
    {
        Assert.True(ClipboardPrivacy.IsExcluded(true, null));
        Assert.True(ClipboardPrivacy.IsExcluded(false, 0));
        Assert.False(ClipboardPrivacy.IsExcluded(false, 1));
        Assert.False(ClipboardPrivacy.IsExcluded(false, null));
    }
}

public class ClipboardViewTests
{
    static readonly DateTime T = new(2026, 1, 1, 14, 3, 0);
    static ClipEntry Entry(long id, string text, ClipKind kind = ClipKind.Text, string? detail = null) =>
        new(id, new ClipItem(kind, text, detail), T);

    [Fact]
    public void One_line_collapses_whitespace_and_ellipsizes()
    {
        Assert.Equal("a b c d", ClipboardView.OneLine("  a\r\n\tb   c\nd \n"));
        Assert.Equal("abcd…", ClipboardView.OneLine("abcdefgh", 5));
        Assert.Equal("abc", ClipboardView.OneLine("abc", 5));
    }

    [Fact]
    public void Empty_tile_says_nothing_copied()
    {
        var tile = ClipboardView.Tile([]);

        Assert.Equal("Clipboard", tile.Title);
        Assert.Equal("Nothing copied yet", tile.Subtitle);
    }

    [Fact]
    public void Text_tile_shows_one_line_time_and_length()
    {
        var tile = ClipboardView.Tile([Entry(2, "hello\n  world"), Entry(1, "older")]);

        Assert.Equal("hello world", tile.Title);
        Assert.Equal("Copied 14:03 · 13 chars", tile.Subtitle); // the raw text counts, newline and spaces included
        Assert.Equal("hello\n  world", tile.Tooltip);
    }

    [Fact]
    public void One_char_is_singular_and_the_tooltip_is_cut()
    {
        Assert.Equal("Copied 14:03 · 1 char", ClipboardView.Tile([Entry(1, "x")]).Subtitle);
        Assert.Equal(300, ClipboardView.Tile([Entry(1, new string('y', 1000))]).Tooltip!.Length);
    }

    [Fact]
    public void Images_and_files_use_their_own_subtitle()
    {
        Assert.Equal("Image 800×600", ClipboardView.Tile([Entry(1, "Image 800×600", ClipKind.Image, "Image 800×600")]).Subtitle);
        var files = ClipboardView.Tile([Entry(1, "3 files: a.txt", ClipKind.Files, "3 files")]);
        Assert.Equal("3 files: a.txt", files.Title);
        Assert.Equal("3 files", files.Subtitle);
    }

    [Fact]
    public void Popup_rows_have_actions_for_text_only()
    {
        var flyout = ClipboardView.Flyout([
            Entry(7, "copied text"), Entry(6, "Image 4×3", ClipKind.Image, "Image 4×3"), Entry(5, "2 files: a.txt", ClipKind.Files, "2 files"),
        ]);

        Assert.Equal("3 recent copies", flyout.Subtitle);
        Assert.Equal(new string?[] { "copy:7", null, null }, flyout.Rows.Select(r => r.Action));
        Assert.All(flyout.Rows, r => Assert.Equal("14:03", r.Value));
        Assert.Equal(new[] { ClipboardSave.SaveAction, ClipboardView.ClearAction }, flyout.Actions.Select(a => a.Id));
        Assert.True(flyout.StayOpen);
    }

    [Fact]
    public void Popup_preview_is_about_forty_chars()
    {
        var row = Assert.Single(ClipboardView.Flyout([Entry(1, new string('a', 100))]).Rows);

        Assert.Equal(40, row.Text.Length);
        Assert.EndsWith("…", row.Text);
    }

    [Theory]
    [InlineData("copy:12", 12L)]
    [InlineData("copy:", null)]
    [InlineData("copy:-1", null)]
    [InlineData("copy:x", null)]
    [InlineData("clear", null)]
    public void Copy_ids_parse(string action, long? id) => Assert.Equal(id, ClipboardView.CopyId(action));
}

public class ClipboardConnectorTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("{}", 15)]
    [InlineData("""{ "history": 5 }""", 5)]
    [InlineData("""{ "history": 0 }""", 1)]
    [InlineData("""{ "history": 500 }""", 50)]
    [InlineData("""{ "history": "ten" }""", 15)]
    public void History_comes_from_settings_clamped(string settings, int size) =>
        Assert.Equal(size, ClipboardConnector.HistorySize(Json(settings), new CollectingLog()));

    [Fact]
    public void A_bad_history_value_is_logged()
    {
        var log = new CollectingLog();

        ClipboardConnector.HistorySize(Json("""{ "history": "ten" }"""), log);

        Assert.Contains(log.Snapshot(), m => m.StartsWith("WARN") && m.Contains("history"));
    }

    sealed class FakeClipboard : IClipboardAccess
    {
        readonly object _lock = new();
        uint _sequence = 1;
        ClipRead _current = new(null);
        public List<string> Written { get; } = [];
        public bool WriteFails { get; set; }
        public ClipSaveRead ForSave { get; set; }
        public ClipSaveRead ReadForSave() { lock (_lock) return ForSave; }

        public uint SequenceNumber { get { lock (_lock) return _sequence; } }
        public ClipRead Read() { lock (_lock) return _current; }

        public void Copy(string text) => Set(new ClipRead(new ClipItem(ClipKind.Text, text)));
        public void Set(ClipRead read) { lock (_lock) { _current = read; _sequence++; } }

        public uint? WriteText(string text)
        {
            lock (_lock)
            {
                if (WriteFails) return null;
                Written.Add(text);
                _current = new ClipRead(new ClipItem(ClipKind.Text, text));
                return ++_sequence;
            }
        }
    }

    static readonly DateTime T = new(2026, 1, 1, 14, 3, 0);

    static ClipboardConnector Connector(FakeClipboard clipboard) => new(clipboard, TimeSpan.FromMilliseconds(10), () => T);

    [Fact]
    public async Task Existing_clipboard_text_and_later_copies_show_on_the_tile()
    {
        var clipboard = new FakeClipboard();
        clipboard.Copy("first");
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);

        clipboard.Copy("second");
        await Eventually.Holds(() => ctx.Updates[^1].Title == "second");

        var flyout = (await connector.GetFlyoutAsync(CancellationToken.None))!;
        Assert.Equal(new[] { "second", "first" }, flyout.Rows.Select(r => r.Text));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task A_private_copy_is_not_recorded_and_not_logged()
    {
        var clipboard = new FakeClipboard();
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);

        clipboard.Set(new ClipRead(null, Excluded: true));
        clipboard.Copy("visible");
        await Eventually.Holds(() => ctx.Updates[^1].Title == "visible");

        Assert.Single((await connector.GetFlyoutAsync(CancellationToken.None))!.Rows);
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task A_busy_clipboard_is_read_again_on_the_next_poll()
    {
        var clipboard = new FakeClipboard();
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);

        clipboard.Set(new ClipRead(null, Retry: true));
        await Task.Delay(60);
        clipboard.Set(new ClipRead(new ClipItem(ClipKind.Text, "late")));
        await Eventually.Holds(() => ctx.Updates[^1].Title == "late");

        await connector.DisposeAsync();
    }

    [Fact]
    public async Task Copy_action_writes_the_text_and_moves_it_to_the_top_without_a_duplicate()
    {
        var clipboard = new FakeClipboard();
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);
        clipboard.Copy("one");
        await Eventually.Holds(() => ctx.Updates[^1].Title == "one");
        clipboard.Copy("two");
        await Eventually.Holds(() => ctx.Updates[^1].Title == "two");
        var oneRow = (await connector.GetFlyoutAsync(CancellationToken.None))!.Rows.Single(r => r.Text == "one");

        await connector.OnActionAsync(oneRow.Action!, CancellationToken.None);
        await Task.Delay(80); // several polls: our own write must not come back as a second copy

        Assert.Equal(new[] { "one" }, clipboard.Written);
        Assert.Equal("one", ctx.Updates[^1].Title);
        Assert.Equal(new[] { "one", "two" }, (await connector.GetFlyoutAsync(CancellationToken.None))!.Rows.Select(r => r.Text));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task Copy_action_for_an_entry_that_is_gone_or_when_the_write_fails_changes_nothing()
    {
        var clipboard = new FakeClipboard { WriteFails = true };
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        clipboard.Copy("one");
        await connector.StartAsync(ctx, CancellationToken.None);
        var action = (await connector.GetFlyoutAsync(CancellationToken.None))!.Rows[0].Action!;

        await connector.OnActionAsync(action, CancellationToken.None);
        await connector.OnActionAsync("copy:999", CancellationToken.None);

        Assert.Empty(clipboard.Written);
        Assert.Contains(ctx.Logged.Snapshot(), m => m.StartsWith("WARN"));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task Clear_empties_the_history_and_the_tile_and_the_clipboard_is_not_read_back()
    {
        var clipboard = new FakeClipboard();
        clipboard.Copy("secret-ish");
        var ctx = new TestTileContext();
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);

        await connector.OnActionAsync(ClipboardView.ClearAction, CancellationToken.None);
        await Task.Delay(80);

        Assert.Equal("Nothing copied yet", ctx.Updates[^1].Subtitle);
        Assert.Empty((await connector.GetFlyoutAsync(CancellationToken.None))!.Rows);
        Assert.DoesNotContain(ctx.Logged.Snapshot(), m => m.Contains("secret-ish"));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task The_history_setting_caps_the_list()
    {
        var clipboard = new FakeClipboard();
        var ctx = new TestTileContext(settingsJson: """{ "history": 2 }""");
        var connector = Connector(clipboard);
        await connector.StartAsync(ctx, CancellationToken.None);

        foreach (var s in new[] { "a", "b", "c" })
        {
            clipboard.Copy(s);
            await Eventually.Holds(() => ctx.Updates[^1].Title == s);
        }

        Assert.Equal(new[] { "c", "b" }, (await connector.GetFlyoutAsync(CancellationToken.None))!.Rows.Select(r => r.Text));
        await connector.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        var connector = Connector(new FakeClipboard());
        await connector.StartAsync(new TestTileContext(), CancellationToken.None);

        await connector.DisposeAsync();
        await connector.DisposeAsync();
    }

    [Fact]
    public void Save_names_carry_the_time_and_count_up_when_taken()
    {
        var now = new DateTime(2026, 11, 5, 14, 30, 12);
        var taken = new HashSet<string> { "clipboard-2026-11-05-143012.png", "clipboard-2026-11-05-143012-2.png" };

        Assert.Equal("clipboard-2026-11-05-143012.txt", ClipboardSave.FileName(now, ".txt", taken.Contains));
        Assert.Equal("clipboard-2026-11-05-143012-3.png", ClipboardSave.FileName(now, ".png", taken.Contains));
    }

    [Fact]
    public void Save_folder_comes_from_settings_with_variables_expanded_else_pictures()
    {
        using var doc = JsonDocument.Parse("""{ "saveFolder": " \"%TEMP%\\clips\" " }""");
        using var empty = JsonDocument.Parse("""{ "saveFolder": "" }""");

        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "clips"), ClipboardSave.Folder(doc.RootElement));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Clipboard"), ClipboardSave.Folder(empty.RootElement));
    }

    /// <summary>A 2x1 32-bit BI_RGB DIB: 40-byte header, then two pixels (B, G, R, A) with alpha 0, as most apps copy them.</summary>
    static byte[] TinyDib()
    {
        var dib = new byte[40 + 8];
        BitConverter.GetBytes(40).CopyTo(dib, 0);           // header size
        BitConverter.GetBytes(2).CopyTo(dib, 4);            // width
        BitConverter.GetBytes(1).CopyTo(dib, 8);            // height (bottom-up)
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);   // planes
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);  // bits per pixel
        new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }.CopyTo(dib, 40); // red, blue
        return dib;
    }

    [Fact]
    public void A_DIB_becomes_a_BMP_file_with_the_pixel_offset_after_header_masks_and_palette()
    {
        var bmp = ClipboardSave.BmpFromDib(TinyDib())!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(14 + 48, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(14 + 40, BitConverter.ToInt32(bmp, 10));

        var bitfields = TinyDib();
        BitConverter.GetBytes(3u).CopyTo(bitfields, 16); // BI_BITFIELDS: three masks follow a 40-byte header
        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(ClipboardSave.BmpFromDib(bitfields)!, 10));

        var paletted = TinyDib();
        BitConverter.GetBytes((ushort)8).CopyTo(paletted, 14); // 8 bpp, biClrUsed 0: a 256-entry palette
        Assert.Equal(14 + 40 + 256 * 4, BitConverter.ToInt32(ClipboardSave.BmpFromDib(paletted)!, 10));

        Assert.Null(ClipboardSave.BmpFromDib(new byte[10]));
    }

    [Fact]
    public void Pixels_with_no_alpha_at_all_become_opaque_and_real_alpha_stays()
    {
        var none = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        ClipboardSave.OpaqueIfNoAlpha(none);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, none);

        var some = new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 };
        ClipboardSave.OpaqueIfNoAlpha(some);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 }, some);
    }

    static async Task<(ClipboardConnector Connector, TestTileContext Ctx, FakeClipboard Clip)> SaveSetup(string folder)
    {
        var clip = new FakeClipboard();
        var ctx = new TestTileContext(settingsJson: $$"""{ "saveFolder": "{{folder.Replace('\\', '/')}}" }""");
        var c = new ClipboardConnector(clip, TimeSpan.FromHours(1), () => new DateTime(2026, 11, 5, 14, 30, 12));
        await c.StartAsync(ctx, CancellationToken.None);
        return (c, ctx, clip);
    }

    [Fact]
    public async Task Save_writes_text_as_UTF8_and_the_popup_says_where()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.File("out"), "clips"); // created on save
        var (c, _, clip) = await SaveSetup(folder);
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Text: "héllo");

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        var path = Path.Combine(folder, "clipboard-2026-11-05-143012.txt");
        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), File.ReadAllBytes(path)); // no BOM
        var popup = (await c.GetFlyoutAsync(CancellationToken.None))!;
        Assert.True(popup.StayOpen);
        Assert.Equal(new[] { ClipboardSave.SaveAction, ClipboardView.ClearAction }, popup.Actions.Select(a => a.Id));
        Assert.Equal(("Saved clipboard-2026-11-05-143012.txt", ClipboardSave.ShowSavedAction), (popup.Rows[^1].Text, popup.Rows[^1].Action!));
    }

    [Fact]
    public async Task Save_writes_an_image_as_PNG_when_there_is_also_text()
    {
        using var dir = new TempDir();
        var (c, _, clip) = await SaveSetup(dir.File("clips"));
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Dib: TinyDib(), Text: "also text");

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        var png = File.ReadAllBytes(Path.Combine(dir.File("clips"), "clipboard-2026-11-05-143012.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    [Theory]
    [InlineData(false, true)]  // a copied file list
    [InlineData(true, false)]  // marked private
    [InlineData(false, false)] // empty
    public async Task Nothing_to_save_writes_nothing(bool excluded, bool files)
    {
        using var dir = new TempDir();
        var (c, _, clip) = await SaveSetup(dir.File("clips"));
        await using var _ = c;
        clip.ForSave = new ClipSaveRead(Text: excluded ? "secret" : null, Excluded: excluded, Files: files);

        await c.OnActionAsync(ClipboardSave.SaveAction, CancellationToken.None);

        Assert.False(Directory.Exists(dir.File("clips")));
        var last = (await c.GetFlyoutAsync(CancellationToken.None))!.Rows[^1];
        Assert.Equal("Nothing to save", last.Text);
        Assert.Null(last.Action);
    }
}
