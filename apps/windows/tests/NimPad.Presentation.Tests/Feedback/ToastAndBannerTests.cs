using Microsoft.Extensions.Time.Testing;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Feedback;

public sealed class ToastAndBannerTests
{
    private readonly FakeTimeProvider _clock = new(ShellHarness.Start);
    private readonly ToastHostViewModel _toasts;

    public ToastAndBannerTests()
    {
        _toasts = new ToastHostViewModel(new ImmediateUiDispatcher(), _clock);
    }

    [Fact]
    public void AToastDismissesItselfAfterSixSeconds()
    {
        var toast = _toasts.Show("Link copied");

        _clock.Advance(TimeSpan.FromMilliseconds(5999));
        Assert.Contains(toast, _toasts.Toasts);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(_toasts.Toasts);
        Assert.True(toast.IsDismissed);
    }

    [Fact]
    public void AtMostThreeToastsShowAndTheOldestGoesFirst()
    {
        var shown = Enumerable.Range(1, 4).Select(i => _toasts.Show($"Toast {i}")).ToList();

        Assert.Equal(["Toast 2", "Toast 3", "Toast 4"], _toasts.Toasts.Select(toast => toast.Message));
        Assert.True(shown[0].IsDismissed);
    }

    [Fact]
    public void HoveringPausesTheCountdownAndLeavingResumesIt()
    {
        var toast = _toasts.Show("Closed “notes.md”", ToastKind.Neutral, "Undo", "Ctrl+Shift+T", () => Task.CompletedTask);
        _clock.Advance(TimeSpan.FromSeconds(4));

        toast.Pause();
        Assert.True(toast.IsPaused);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains(toast, _toasts.Toasts);

        toast.Resume();
        _clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Contains(toast, _toasts.Toasts);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public void ResumingAfterTheTimeRanOutStillShowsTheToastBriefly()
    {
        var toast = _toasts.Show("Saved");
        _clock.Advance(TimeSpan.FromSeconds(5.9));
        toast.Pause();

        toast.Resume();
        _clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Contains(toast, _toasts.Toasts);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task TheActionRunsOnceAndDismissesTheToast()
    {
        var runs = 0;
        var toast = _toasts.Show("Moved “X” to Trash", ToastKind.Neutral, "Undo", null, () =>
        {
            runs++;
            return Task.CompletedTask;
        });

        Assert.True(toast.HasAction);
        await toast.ActionCommand.ExecuteAsync(null);

        Assert.Equal(1, runs);
        Assert.Empty(_toasts.Toasts);
        Assert.False(toast.ActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailingActionBecomesAnErrorToast()
    {
        var toast = _toasts.Show("Closed", ToastKind.Neutral, "Undo", null, () => throw new IOException("The disk is full."));

        await toast.ActionCommand.ExecuteAsync(null);

        var error = Assert.Single(_toasts.Toasts);
        Assert.Equal(ToastKind.Error, error.Kind);
        Assert.Equal("The disk is full.", error.Message);
    }

    [Fact]
    public void ToastsOfTheSameGroupReplaceEachOther()
    {
        _toasts.Show("Link copied");
        _toasts.Show("Closed “a”", groupKey: "closed");
        _toasts.Show("Closed “b”", groupKey: "closed");

        Assert.Equal(["Link copied", "Closed “b”"], _toasts.Toasts.Select(toast => toast.Message));
        Assert.Null(_toasts.Toasts[0].ActionText);
    }

    [Fact]
    public async Task BannersAreKeyedByTheirCause()
    {
        var banners = new BannerHostViewModel();
        var reloads = 0;

        var first = banners.Show("disk:1", BannerKind.Warning, "“a.txt” changed on disk.", "Reload", () =>
        {
            reloads++;
            return Task.CompletedTask;
        });
        banners.Show("other", BannerKind.Info, "Something else");
        var replacement = banners.Show("disk:1", BannerKind.Warning, "“a.txt” changed on disk again.", "Reload", () =>
        {
            reloads += 10;
            return Task.CompletedTask;
        });

        Assert.Equal(["disk:1", "other"], banners.Banners.Select(banner => banner.Key));
        Assert.Same(replacement, banners.Banners[0]);
        Assert.NotSame(first, replacement);
        Assert.True(banners.Contains("other"));

        await replacement.ActionCommand.ExecuteAsync(null);
        Assert.Equal(10, reloads);
        Assert.Equal(["other"], banners.Banners.Select(banner => banner.Key));

        Assert.True(banners.Remove("other"));
        Assert.False(banners.Remove("other"));
        Assert.Empty(banners.Banners);

        var sticky = banners.Show("save", BannerKind.Error, "Couldn't save.", canDismiss: false);
        Assert.False(sticky.CanDismiss);
        Assert.False(sticky.DismissCommand.CanExecute(null));
        Assert.False(sticky.HasAction);
        Assert.Null(sticky.ActionText);
    }
}
