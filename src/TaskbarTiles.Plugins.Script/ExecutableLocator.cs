namespace TaskbarTiles.Plugins.Script;

public static class ExecutableLocator
{
    /// <summary>Full path of fileName in the first PATH folder that has it, or null.</summary>
    public static string? FindOnPath(string fileName, string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var folder = entry.Trim('"').Trim();
                // An empty or relative entry would be probed against the working directory.
                if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }
        return null;
    }

    /// <summary>PowerShell 7 (pwsh.exe) when it is on PATH, else Windows PowerShell.</summary>
    public static string PowerShell(string? pathVariable = null) => FindOnPath("pwsh.exe", pathVariable) ?? "powershell.exe";
}
