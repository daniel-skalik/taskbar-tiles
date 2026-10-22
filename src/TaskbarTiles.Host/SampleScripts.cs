using System.IO;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Host;

/// <summary>Copies the sample scripts into the data folder on first run.</summary>
public static class SampleScripts
{
    /// <summary>
    /// Copies every file in sourceDir into targetDir, but only when targetDir does not exist yet, so samples the
    /// user deleted or edited are never brought back. Returns true when it copied. Never throws for IO problems.
    /// </summary>
    public static bool InstallIfAbsent(string sourceDir, string targetDir, ITileLog log)
    {
        if (Directory.Exists(targetDir) || !Directory.Exists(sourceDir)) return false;
        try
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
            log.Info($"Installed sample scripts into {targetDir}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Could not install sample scripts into {targetDir}", ex);
            return false;
        }
    }
}
