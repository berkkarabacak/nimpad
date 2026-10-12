using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace NimPad.App.Controls;

/// <summary>
/// One key cap ("Ctrl", "Shift", "N"): radius 4, 1 px Line2 border with a 2 px bottom edge, monospace caption
/// text, deck pages 12 and 25. For a whole gesture use <see cref="ShortcutKeys"/>.
/// </summary>
public class KeyCap : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(KeyCap), new FrameworkPropertyMetadata(string.Empty));

    static KeyCap()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyCap), new FrameworkPropertyMetadata(typeof(KeyCap)));
        FocusableProperty.OverrideMetadata(typeof(KeyCap), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The key label, e.g. "Ctrl".</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new KeyCapAutomationPeer(this);

    private sealed class KeyCapAutomationPeer(KeyCap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(KeyCap);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((KeyCap)Owner).Text : name;
        }
    }
}

/// <summary>
/// A keyboard gesture drawn as a row of <see cref="KeyCap"/>s 4 px apart: <c>Gesture="Ctrl+Shift+T"</c>.
/// "Ctrl++" renders Ctrl and +. Read by screen readers as the gesture text.
/// </summary>
public class ShortcutKeys : Control
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(ShortcutKeys),
        new FrameworkPropertyMetadata(string.Empty, OnGestureChanged));

    private static readonly DependencyPropertyKey KeysPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Keys), typeof(IReadOnlyList<string>), typeof(ShortcutKeys),
        new FrameworkPropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty KeysProperty = KeysPropertyKey.DependencyProperty;

    static ShortcutKeys()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ShortcutKeys), new FrameworkPropertyMetadata(typeof(ShortcutKeys)));
        FocusableProperty.OverrideMetadata(typeof(ShortcutKeys), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The gesture, keys joined with "+": "Ctrl+Shift+T", "Shift+F10", "Ctrl++".</summary>
    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>The individual key labels parsed from <see cref="Gesture"/>.</summary>
    public IReadOnlyList<string> Keys => (IReadOnlyList<string>)GetValue(KeysProperty);

    /// <summary>Splits "Ctrl+Shift+T" into its keys; a "+" with nothing before it is the plus key itself.</summary>
    public static IReadOnlyList<string> ParseGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return Array.Empty<string>();
        }

        var keys = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < gesture.Length; i++)
        {
            var c = gesture[i];
            if (c != '+')
            {
                current.Append(c);
                continue;
            }

            if (current.ToString().Trim().Length > 0)
            {
                keys.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            // "+" right after a separator (or at the start) is the plus key; swallow the separator after it.
            keys.Add("+");
            current.Clear();
            if (i + 1 < gesture.Length && gesture[i + 1] == '+')
            {
                i++;
            }
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            keys.Add(last);
        }

        return keys;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ShortcutKeysAutomationPeer(this);

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(KeysPropertyKey, ParseGesture((string?)e.NewValue));

    private sealed class ShortcutKeysAutomationPeer(ShortcutKeys owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(ShortcutKeys);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((ShortcutKeys)Owner).Gesture : name;
        }

        // The key caps are presentation only; the gesture is announced once, by this element.
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
