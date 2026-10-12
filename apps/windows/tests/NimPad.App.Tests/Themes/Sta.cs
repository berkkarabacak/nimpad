using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace NimPad.App.Tests.Themes;

/// <summary>Runs WPF code on a fresh STA thread and rethrows its failure on the test thread.</summary>
internal static class Sta
{
    public const string WindowsOnly = "WPF runs only on Windows.";

    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
