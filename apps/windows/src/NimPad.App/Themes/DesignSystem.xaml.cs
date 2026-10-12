using System.Windows;

namespace NimPad.App.Themes;

/// <summary>
/// Typography, metrics, icons and every control style (Themes/Controls.*.xaml). Merge it after a token set
/// (<see cref="TokensLight"/> or <see cref="TokensDark"/>); see Themes/README.md.
/// </summary>
public partial class DesignSystem : ResourceDictionary
{
    public DesignSystem()
    {
        InitializeComponent();
    }
}
