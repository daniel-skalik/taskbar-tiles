using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host;

/// <summary>Copies the sample scripts into the data folder on first run.</summary>
public static class SampleScripts
{
    /// <summary>
    /// Copies every file in sourceDir into targetDir, but only when targetDir does not exist yet, so samples the
    /// user deleted or edited are never brought back. The copy goes to a temporary sibling folder that is then
    /// moved into place, so a half-finished copy can never block a later install. Returns true when it installed.
    /// Never throws.
    /// </summary>
    public static bool InstallIfAbsent(string sourceDir, string targetDir, ITileLog log)
    {
        string? temp = null;
        try
        {
            if (Directory.Exists(targetDir) || !Directory.Exists(sourceDir)) return false;
            var full = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            temp = full + ".installing-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temp);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
            if (Directory.Exists(targetDir)) return false; // appeared meanwhile; keep what is there
            Retry(() => Directory.Move(temp, full));
            temp = null;
            log.Info($"Installed sample scripts into {targetDir}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Could not install sample scripts into {targetDir}", ex);
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                try { Retry(() => Directory.Delete(temp, recursive: true)); }
                catch (Exception) { /* leftover temp folder is harmless; its name never matches the target */ }
            }
        }
    }

    // Defender or the indexer can hold a freshly written file open for a moment, which fails the rename or delete.
    static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(100); }
        }
    }
}
