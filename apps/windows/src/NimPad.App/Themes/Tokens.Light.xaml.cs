using System.Windows;

namespace NimPad.App.Themes;

/// <summary>
/// The light token set from docs/design/tokens.md: a Color ("&lt;Name&gt;Color") and a frozen brush
/// ("&lt;Name&gt;Brush") per token. <see cref="Theming.ThemeService"/> keeps one token set first in the
/// application's merged dictionaries and swaps it on theme change.
/// </summary>
public partial class TokensLight : ResourceDictionary
{
    public TokensLight()
    {
        InitializeComponent();
    }
}
