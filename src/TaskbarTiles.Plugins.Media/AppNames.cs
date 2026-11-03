using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TaskbarTiles.Plugins.Media;

/// <summary>
/// The name an app goes by in the Start menu, from the id its media session reports: Firefox reports
/// "308046B0AF4A39CB", the Windows media app "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic". Looked up once per
/// id in the shell's Applications folder; null when the shell does not know the id (Spotify reports "Spotify.exe").
/// </summary>
public static class AppNames
{
    static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? DisplayName(string? aumid) =>
        string.IsNullOrWhiteSpace(aumid) ? null : Cache.GetOrAdd(aumid.Trim(), Lookup);

    /// <summary>
    /// Whether a session belongs to the app the tile follows: <paramref name="app"/> is part of its id or of its Start
    /// menu name, ignoring case, so "Firefox", "Chrome" and the old "Spotify" all work.
    /// </summary>
    public static bool Matches(string? aumid, string? displayName, string app) =>
        app.Length > 0 && ((aumid?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false)
            || (displayName?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false));

    static string? Lookup(string aumid)
    {
        try
        {
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, typeof(IShellItem).GUID, out var item) != 0) return null;
            try
            {
                if (item.GetDisplayName(SigdnNormalDisplay, out var name) != 0 || name == IntPtr.Zero) return null;
                try
                {
                    var text = Marshal.PtrToStringUni(name);
                    // An unknown id still parses, named by the id itself: that is no name.
                    return string.IsNullOrWhiteSpace(text) || text.Equals(aumid, StringComparison.OrdinalIgnoreCase) ? null : text;
                }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception) { return null; } // no shell, or an id it cannot parse: fall back to the id
    }

    const uint SigdnNormalDisplay = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
}
