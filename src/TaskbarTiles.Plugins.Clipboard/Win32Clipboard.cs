using System.Runtime.InteropServices;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>
/// The real clipboard through user32 and kernel32 alone, so the plugin needs neither WPF nor WinForms. Any thread may
/// call it (OpenClipboard needs no STA), but only one at a time; the connector's gate sees to that.
/// </summary>
public sealed class Win32Clipboard : IClipboardAccess
{
    const uint CfBitmap = 2, CfDib = 8, CfUnicodeText = 13, CfHdrop = 15, CfDibV5 = 17;
    const uint GmemMoveable = 0x0002;
    const int OpenAttempts = 6, OpenRetryMs = 15;
    const int MaxChars = 1_000_000; // a bigger copy is skipped rather than held in memory six times over

    // Registered once; the ids are fixed for the session.
    static readonly uint ExcludeId = RegisterClipboardFormat(ClipboardPrivacy.ExcludeFormat);
    static readonly uint CanIncludeId = RegisterClipboardFormat(ClipboardPrivacy.CanIncludeFormat);

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public ClipRead Read()
    {
        if (!Open()) return new ClipRead(null, Retry: true);
        try
        {
            if (ClipboardPrivacy.IsExcluded(IsClipboardFormatAvailable(ExcludeId), ReadDword(CanIncludeId)))
                return new ClipRead(null, Excluded: true);
            return new ClipRead(ReadFiles() ?? ReadText() ?? ReadImage());
        }
        finally { CloseClipboard(); }
    }

    public uint? WriteText(string text)
    {
        if (!Open()) return null;
        try
        {
            // The clipboard owns the memory once SetClipboardData succeeds; until then it is ours to free.
            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (memory == 0) return null;
            var target = GlobalLock(memory);
            if (target == 0) { GlobalFree(memory); return null; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally { GlobalUnlock(memory); }

            if (!EmptyClipboard() || SetClipboardData(CfUnicodeText, memory) == 0)
            {
                GlobalFree(memory);
                return null;
            }
            return GetClipboardSequenceNumber();
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Other apps hold the clipboard for a moment while they copy, so try a few times before giving up.</summary>
    static bool Open()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(OpenRetryMs);
        }
        return false;
    }

    static ClipItem? ReadText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == 0) return null;
        var size = (long)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            // Bounded by the block's size in case the owner left off the terminator.
            var chars = (int)Math.Min(size / 2, MaxChars + 1L);
            var text = Marshal.PtrToStringUni(pointer, chars);
            var end = text.IndexOf('\0');
            if (end >= 0) text = text[..end];
            if (text.Length > MaxChars || string.IsNullOrWhiteSpace(text)) return null;
            return new ClipItem(ClipKind.Text, text);
        }
        finally { GlobalUnlock(handle); }
    }

    static ClipItem? ReadFiles()
    {
        if (!IsClipboardFormatAvailable(CfHdrop)) return null;
        var drop = GetClipboardData(CfHdrop);
        if (drop == 0) return null;
        var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        if (count == 0) return null;
        var path = new char[1024];
        var length = DragQueryFile(drop, 0, path, (uint)path.Length);
        var first = Path.GetFileName(new string(path, 0, (int)length));
        var noun = count == 1 ? "1 file" : $"{count} files";
        return new ClipItem(ClipKind.Files, $"{noun}: {first}", noun);
    }

    static ClipItem? ReadImage()
    {
        uint format = IsClipboardFormatAvailable(CfDibV5) ? CfDibV5 : IsClipboardFormatAvailable(CfDib) ? CfDib : 0;
        if (format == 0) return IsClipboardFormatAvailable(CfBitmap) ? Image(0, 0) : null;
        var handle = GetClipboardData(format);
        var pointer = handle == 0 ? 0 : GlobalLock(handle);
        if (pointer == 0) return Image(0, 0);
        try
        {
            // Both DIB headers start the same: size (4), width (4), height (4, negative when stored top-down).
            return Image(Marshal.ReadInt32(pointer, 4), Math.Abs(Marshal.ReadInt32(pointer, 8)));
        }
        finally { GlobalUnlock(handle); }
    }

    static ClipItem Image(int width, int height)
    {
        var label = width > 0 && height > 0 ? $"Image {width}×{height}" : "Image";
        return new ClipItem(ClipKind.Image, label, label);
    }

    /// <summary>The first four bytes of a registered format as a number, or null when the format is absent or too small.</summary>
    static uint? ReadDword(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var handle = GetClipboardData(format);
        if (handle == 0 || GlobalSize(handle) < 4) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try { return (uint)Marshal.ReadInt32(pointer); }
        finally { GlobalUnlock(handle); }
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(nint newOwner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(nint drop, uint index, [Out] char[]? buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nuint GlobalSize(nint memory);
}
