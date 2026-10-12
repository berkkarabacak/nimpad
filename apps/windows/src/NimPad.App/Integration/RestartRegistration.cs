using System.Diagnostics;
using System.Runtime.Versioning;

namespace NimPad.App.Integration;

/// <summary>
/// Asks Windows to restart NimPad after a crash, a hang, an update or a reboot that closed it
/// (<c>RegisterApplicationRestart</c>). Windows restarts only processes that ran for at least 60 seconds and passes the
/// registered arguments (<c>--restarted</c> plus the options that select the same data). After a crash the shell
/// shows the "recovered unsaved changes" banner from the recovery buffers, so nothing else is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RestartRegistration
{
    /// <summary>RESTART_MAX_CMD_LINE: the longest argument string, including its terminating null.</summary>
    public const int MaxArgumentsLength = 1024;

    /// <summary>Registers the restart; logs and returns false when Windows refuses.</summary>
    /// <param name="arguments">The arguments for the restarted process (without the executable).</param>
    public static bool Register(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length >= MaxArgumentsLength)
        {
            Trace.TraceWarning($"The restart arguments are longer than {MaxArgumentsLength - 1} characters; restart is not registered.");
            return false;
        }

        try
        {
            var result = NativeMethods.RegisterApplicationRestart(arguments, 0);
            if (result != 0)
            {
                Trace.TraceWarning($"RegisterApplicationRestart failed with 0x{result:X8}.");
            }

            return result == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning($"RegisterApplicationRestart is unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes the registration (for example when restarting is not wanted any more).</summary>
    public static bool Unregister()
    {
        try
        {
            return NativeMethods.UnregisterApplicationRestart() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
