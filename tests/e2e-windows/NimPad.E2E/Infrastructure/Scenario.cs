using System.Runtime.CompilerServices;
using Xunit;

namespace NimPad.E2E.Infrastructure;

/// <summary>
/// One end-to-end test run. It owns the temporary data directories and every app it launched. When the test body
/// throws, it first saves a screenshot, a UI Automation tree dump, the error and the app logs to
/// <c>NIMPAD_E2E_ARTIFACTS</c>, then rethrows. Afterwards it always ends the apps and deletes its temporary folders,
/// so no test ever touches the real NimPad profile.
/// </summary>
internal sealed class Scenario : IDisposable
{
    private readonly string _testName;
    private readonly string _root;
    private readonly List<AppSession> _sessions = [];
    private readonly List<string> _dataDirectories = [];
    private readonly Lazy<string> _executable = new(AppLocator.FindExecutable);
    private int _folders;

    private Scenario(string testName)
    {
        _testName = testName;
        _root = Path.Combine(Path.GetTempPath(), "NimPad.E2E", $"{testName}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Runs a test body in a new scenario. Call it after <c>Assert.SkipUnless(OperatingSystem.IsWindows(), …)</c>.
    /// </summary>
    public static void Run(Action<Scenario> body, [CallerMemberName] string testName = "")
    {
        using var scenario = new Scenario(testName);
        try
        {
            body(scenario);
        }
        catch (Exception failure)
        {
            scenario.CaptureFailure(failure);
            throw;
        }
    }

    /// <summary>A new, empty data directory for <c>--data-dir</c> (a brand-new profile).</summary>
    public string CreateDataDirectory()
    {
        var path = CreateFolder("data");
        _dataDirectories.Add(path);
        return path;
    }

    /// <summary>A new, empty scratch folder (for external files).</summary>
    public string CreateFolder(string purpose)
    {
        var path = Path.Combine(_root, $"{purpose}{++_folders}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Launches NimPad on <paramref name="dataDirectory"/> with the test flags and <paramref name="files"/>, waits until
    /// it has started, sizes the window and brings it to the front.
    /// </summary>
    public AppSession Launch(string dataDirectory, params string[] files)
    {
        var session = AppSession.Start(_executable.Value, dataDirectory, files, _sessions.Count + 1);
        _sessions.Add(session);
        session.WaitUntilReady();
        return session;
    }

    /// <summary>Ends every app still running and deletes the temporary folders.</summary>
    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        // The processes are gone, but Windows may release their file handles a moment later.
        Ui.TryWaitUntil(TryDeleteRoot, TimeSpan.FromSeconds(5));
    }

    private void CaptureFailure(Exception failure)
    {
        var folder = Artifacts.CaptureFailure(_testName, failure, _sessions, _dataDirectories);
        if (folder is not null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"UI test artifacts: {folder}");
        }
    }

    private bool TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
