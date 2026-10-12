namespace NimPad.E2E.Infrastructure;

/// <summary>Finds the NimPad executable under test.</summary>
internal static class AppLocator
{
    /// <summary>Environment variable with the full path of NimPad.exe (set by CI).</summary>
    public const string ExecutableVariable = "NIMPAD_EXE";

    /// <summary>
    /// <c>NIMPAD_EXE</c> when set; otherwise the newest of
    /// <c>apps/windows/src/NimPad.App/bin/{Release,Debug}/net10.0-windows/NimPad.exe</c> under the repository root (the
    /// first ancestor of the test binaries that holds <c>global.json</c>). Never skips: a missing app is a failure.
    /// </summary>
    public static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured.Trim().Trim('"'));
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException(
                    $"{ExecutableVariable} points at '{path}', which does not exist. Build the app first " +
                    "(dotnet build apps/windows/NimPad.slnx -c Release).",
                    path);
        }

        var root = FindRepositoryRoot()
            ?? throw new DirectoryNotFoundException(
                $"Could not find the repository root (a folder with global.json) above '{AppContext.BaseDirectory}'. " +
                $"Set {ExecutableVariable} to the full path of NimPad.exe.");
        var candidates = new[] { "Release", "Debug" }
            .Select(configuration => Path.Combine(
                root, "apps", "windows", "src", "NimPad.App", "bin", configuration, "net10.0-windows", "NimPad.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        return candidates.Count > 0
            ? candidates[0]
            : throw new FileNotFoundException(
                "NimPad.exe was not found under apps/windows/src/NimPad.App/bin/{Release,Debug}/net10.0-windows. " +
                $"Build the app first (dotnet build apps/windows/NimPad.slnx -c Release) or set {ExecutableVariable}.");
    }

    /// <summary>The first ancestor of the test binaries that contains <c>global.json</c>, or <see langword="null"/>.</summary>
    public static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
