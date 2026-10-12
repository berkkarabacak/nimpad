using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Platform;

namespace NimPad.App.Integration;

/// <summary>
/// Start with Windows: the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value <c>NimPad</c> =
/// <c>"&lt;exe path&gt;" --background</c> (see <see cref="AutostartCommand"/>). The App starts minimized when it sees
/// <c>--background</c>. Portable copies register wherever their executable is.
/// </summary>
/// <remarks>
/// <see cref="IsEnabled"/> is true only when the value starts this executable and Task Manager's Startup apps page has
/// not turned it off (<c>Explorer\StartupApproved\Run</c>). <see cref="Set"/> also clears that Task Manager mark, so
/// the switch means what it shows. Registry errors propagate; the settings view model reverts the switch and shows a
/// toast.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AutostartService : IAutostartService
{
    /// <summary>The per-user Run key.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager records Run entries the user turned off.</summary>
    public const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>NimPad's value name.</summary>
    public const string DefaultValueName = "NimPad";

    /// <summary>Uses this process's executable and the value name <c>NimPad</c>.</summary>
    public AutostartService()
        : this(ResolveExecutablePath(), DefaultValueName)
    {
    }

    /// <summary>Uses <paramref name="executablePath"/> and <paramref name="valueName"/> (tests use their own name).</summary>
    public AutostartService(string executablePath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ExecutablePath = executablePath;
        ValueName = valueName;
    }

    /// <summary>The executable the Run value starts.</summary>
    public string ExecutablePath { get; }

    /// <summary>The Run value's name.</summary>
    public string ValueName { get; }

    /// <summary>The Run value NimPad writes: <c>"&lt;exe path&gt;" --background</c>.</summary>
    public string Command => AutostartCommand.Build(ExecutablePath);

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (!AutostartCommand.PointsTo(run?.GetValue(ValueName) as string, ExecutablePath))
            {
                return false;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
            return !AutostartCommand.IsTurnedOffByUser(approved?.GetValue(ValueName) as byte[]);
        }
    }

    /// <inheritdoc/>
    public void Set(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException($@"HKCU\{RunKeyPath} could not be opened."))
        {
            if (enabled)
            {
                run.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// The running NimPad executable: <see cref="Environment.ProcessPath"/>, or <c>NimPad.exe</c> next to the app when
    /// it runs under the <c>dotnet</c> host.
    /// </summary>
    public static string ResolveExecutablePath()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process)
            && !string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        return Path.Combine(AppContext.BaseDirectory, "NimPad.exe");
    }
}
