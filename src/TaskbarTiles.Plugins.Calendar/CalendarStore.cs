using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarTiles.Plugins.Calendar;

/// <summary>Where the calendars' secret addresses are kept. Tests stand in for it.</summary>
public interface ICalendarStore
{
    IReadOnlyList<string> Read();
    void Write(IReadOnlyList<string> addresses);
    void Delete();
}

/// <summary>
/// The secret addresses, one per line, in one Windows Credential Manager entry ("TaskbarTiles-GoogleCalendar"),
/// readable only by this Windows user. They are never written anywhere else.
/// </summary>
public sealed class CredentialStore : ICalendarStore
{
    public const string Target = "TaskbarTiles-GoogleCalendar";
    const int Generic = 1, LocalMachine = 2, NotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags, Type;
        public string TargetName, Comment;
        public long LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    public IReadOnlyList<string> Read()
    {
        if (!CredRead(Target, Generic, 0, out var p))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? [] : throw new Win32Exception(error);
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(p);
            var text = c.BlobSize > 0 ? Marshal.PtrToStringUni(c.Blob, c.BlobSize / 2) : "";
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally { CredFree(p); }
    }

    public void Write(IReadOnlyList<string> addresses)
    {
        var bytes = Encoding.Unicode.GetBytes(string.Join('\n', addresses));
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential
            {
                Type = Generic, TargetName = Target, UserName = "calendars", Persist = LocalMachine,
                BlobSize = bytes.Length, Blob = blob, Comment = "Taskbar Tiles: Google Calendar secret addresses",
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(Target, Generic, 0) && Marshal.GetLastWin32Error() is var error && error != NotFound) throw new Win32Exception(error);
    }
}

/// <summary>Downloads a calendar. Tests stand in for it.</summary>
public interface ICalendarSource
{
    /// <summary>The calendar file; throws <see cref="CalendarFetchException"/> with a message that never names the address.</summary>
    Task<string> GetAsync(string address, CancellationToken ct);
}

/// <summary>A download that failed, described without the address (which is a secret).</summary>
public sealed class CalendarFetchException(string message) : Exception(message);

public sealed class HttpCalendarSource : ICalendarSource
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "TaskbarTiles" } },
    };

    public async Task<string> GetAsync(string address, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await Http.GetAsync(address, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { throw new CalendarFetchException("Google Calendar did not answer in time"); }
        catch (HttpRequestException) { throw new CalendarFetchException("could not reach Google Calendar"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CalendarFetchException(response.StatusCode == HttpStatusCode.NotFound
                    ? "not found (404); the address may have been reset"
                    : $"Google Calendar answered {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
