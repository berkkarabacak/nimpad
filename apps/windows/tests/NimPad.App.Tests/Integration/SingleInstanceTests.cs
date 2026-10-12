using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NimPad.App.Integration;
using NimPad.App.Tests.Themes;
using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.App.Tests.Integration;

/// <summary>
/// Two in-process "instances" over a uniquely named mutex and pipe. Mutex ownership is per thread, so each instance
/// acquires on its own thread, as two processes would.
/// </summary>
public sealed class SingleInstanceTests
{
    private static string UniqueName() => "NimPad-test-" + Guid.NewGuid().ToString("N");

    private static string FullPath(string name) => Path.Combine(Path.GetTempPath(), "nimpad single instance", name);

    [Fact]
    public void ASecondInstanceForwardsItsFilesToTheFirstAndGetsAnAcknowledgement()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = UniqueName();
        using var primary = new SingleInstance(name);
        Assert.True(primary.TryAcquire());
        Assert.True(primary.IsPrimary);
        var received = new BlockingCollection<SingleInstanceMessage>();
        primary.StartServer(received.Add);
        Assert.True(primary.IsListening);

        var outcome = OnOtherThread(() =>
        {
            using var secondary = new SingleInstance(name);
            var acquired = secondary.TryAcquire();
            var forwarded = secondary.TryForward(new([FullPath("a.txt"), FullPath("b c.md")]), TimeSpan.FromSeconds(10));
            var again = secondary.TryForward(new([], Activate: false), TimeSpan.FromSeconds(10));
            return (acquired, forwarded, again);
        });

        Assert.False(outcome.acquired);
        Assert.True(outcome.forwarded);
        Assert.True(outcome.again);
        Assert.True(received.TryTake(out var first, TimeSpan.FromSeconds(5)));
        Assert.Equal(new SingleInstanceMessage([FullPath("a.txt"), FullPath("b c.md")]), first);
        Assert.True(received.TryTake(out var second, TimeSpan.FromSeconds(5)));
        Assert.Equal(new SingleInstanceMessage([], Activate: false), second);
    }

    [Fact]
    public void ThePipeIsOwnedByAndOpenOnlyToTheCurrentUser()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        using var identity = WindowsIdentity.GetCurrent();

        var security = SingleInstance.CreatePipeSecurity(identity);
        AssertCurrentUserOnly(security, identity);

        // The pipe Windows created carries exactly that descriptor.
        using var instance = new SingleInstance(UniqueName());
        using var server = instance.CreateServer(first: true);
        AssertCurrentUserOnly(server.GetAccessControl(), identity);

        // The name is taken: another "first" instance is refused, so nobody can squat on it.
        var refused = Record.Exception(() => instance.CreateServer(first: true).Dispose());
        Assert.True(refused is UnauthorizedAccessException or IOException, $"unexpected: {refused}");
    }

    [Fact]
    public void ForwardingWithoutARunningInstanceFailsQuietlyAfterTheTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        using var lonely = new SingleInstance(UniqueName());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(lonely.TryForward(new([FullPath("x.txt")]), TimeSpan.FromMilliseconds(300)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AnInvalidMessageIsIgnoredAndTheServerKeepsServing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = UniqueName();
        using var primary = new SingleInstance(name);
        Assert.True(primary.TryAcquire());
        var received = new BlockingCollection<SingleInstanceMessage>();
        primary.StartServer(received.Add);

        using (var rogue = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly))
        {
            rogue.Connect(10_000);
            rogue.Write([5, 0, 0, 0, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']);
            rogue.Flush();
            Assert.Equal(-1, rogue.ReadByte());
        }

        using var secondary = new SingleInstance(name);
        Assert.True(secondary.TryForward(new([FullPath("ok.txt")]), TimeSpan.FromSeconds(10)));
        Assert.True(received.TryTake(out var message, TimeSpan.FromSeconds(5)));
        Assert.Equal([FullPath("ok.txt")], message.Files);
        Assert.Empty(received);
    }

    [Fact]
    public void ACrashedPrimaryIsReplacedByTheNextLaunch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = UniqueName();

        // The first owner's thread ends without releasing the mutex (as a crashed process would).
        using var crashed = new SingleInstance(name);
        Assert.True(OnOtherThread(crashed.TryAcquire));

        using var next = new SingleInstance(name);
        Assert.True(OnOtherThread(next.TryAcquire));
        Assert.True(next.IsPrimary);
    }

    [Fact]
    public void ReleasingThePrimaryLetsTheNextLaunchBecomePrimary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = UniqueName();
        var first = new SingleInstance(name);
        Assert.True(first.TryAcquire());
        first.StartServer(_ => { });
        Assert.False(OnOtherThread(() =>
        {
            using var probe = new SingleInstance(name);
            return probe.TryAcquire();
        }));

        first.Dispose();
        Assert.False(first.IsListening);

        Assert.True(OnOtherThread(() =>
        {
            using var later = new SingleInstance(name);
            return later.TryAcquire();
        }));
        Assert.Throws<ArgumentException>(() => new SingleInstance(@"bad\name"));
    }

    // Only the user may connect; the owner is the token's default owner (what CurrentUserOnly clients verify).
    private static void AssertCurrentUserOnly(PipeSecurity security, WindowsIdentity identity)
    {
        Assert.Equal(identity.Owner, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights & PipeAccessRights.FullControl);
    }

    private static T OnOtherThread<T>(Func<T> work)
    {
        var result = default(T)!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The other thread failed.", failure);
        }

        return result;
    }
}
