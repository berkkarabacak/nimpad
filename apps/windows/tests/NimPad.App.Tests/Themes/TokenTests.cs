using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NimPad.App.Themes;
using Xunit;

namespace NimPad.App.Tests.Themes;

/// <summary>Every token from docs/design/tokens.md exists in both themes with the exact value.</summary>
public class TokenTests
{
    /// <summary>
    /// Verbatim from docs/design/tokens.md ("#RRGGBB" or "#RRGGBB · N%"), plus CloseHover and Scrim.
    /// Keep in sync with the doc: this table is the regression guard for the token dictionaries.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Expected =
        new Dictionary<string, (string Light, string Dark)>
        {
            ["Chrome"] = ("#EFEFEC", "#252527"),
            ["Surface"] = ("#FBFBFA", "#1B1B1D"),
            ["Raised"] = ("#FFFFFF", "#2B2B2E"),
            ["Text"] = ("#1D1D1B", "#E8E7E3"),
            ["Text2"] = ("#5B5B57", "#A8A7A2"),
            ["Text3"] = ("#686863", "#92918C"),
            ["Hover"] = ("#1D1D1B · 6%", "#FFFFFF · 7%"),
            ["Press"] = ("#1D1D1B · 10%", "#FFFFFF · 11%"),
            ["Sep"] = ("#1D1D1B · 16%", "#FFFFFF · 16%"),
            ["Line"] = ("#1D1D1B · 10%", "#FFFFFF · 9%"),
            ["Line2"] = ("#1D1D1B · 26%", "#FFFFFF · 22%"),
            ["Accent"] = ("#3055D8", "#8CA2FF"),
            ["AccentHover"] = ("#2647BD", "#A3B5FF"),
            ["AccentPressed"] = ("#1F3B9E", "#7389EA"),
            ["AccentSoft"] = ("#E6ECFC", "#8CA2FF · 16%"),
            ["OnAccent"] = ("#FFFFFF", "#0B1230"),
            ["Selection"] = ("#3055D8 · 18%", "#8CA2FF · 30%"),
            ["CurrentLine"] = ("#1D1D1B · 4%", "#FFFFFF · 4%"),
            ["Match"] = ("#F0B928 · 40%", "#F0BE3C · 34%"),
            ["MatchCurrent"] = ("#F09614 · 72%", "#F0A028 · 60%"),
            ["AvatarBg"] = ("#DCE3FB", "#33406F"),
            ["AvatarFg"] = ("#273C99", "#D5DEFF"),
            ["Ok"] = ("#26704A", "#6FCB95"),
            ["OkSoft"] = ("#E3F2EA", "#6FCB95 · 14%"),
            ["Warn"] = ("#8F5200", "#EDB75A"),
            ["WarnSoft"] = ("#FBEFD5", "#EDB75A · 14%"),
            ["Err"] = ("#B3261E", "#FF8F86"),
            ["ErrSoft"] = ("#FBE5E3", "#FF8F86 · 14%"),
            ["DiffAdd"] = ("#289655 · 16%", "#5AC882 · 18%"),
            ["DiffDel"] = ("#C8372D · 14%", "#FF6E64 · 18%"),
            ["SyntaxKey"] = ("#2B5FAE", "#8DB4FF"),
            ["SyntaxString"] = ("#2A7A4B", "#8FD1A5"),
            ["SyntaxNumber"] = ("#A8530F", "#F0B27A"),
            ["SyntaxLiteral"] = ("#8A3FA8", "#C9A0F0"),
            ["SyntaxPunctuation"] = ("#75756F", "#8D8C87"),
            ["SyntaxComment"] = ("#75756F", "#8A8984"),
            ["CloseHover"] = ("#C42B1C", "#C42B1C"),
            ["Scrim"] = ("#000000 · 40%", "#000000 · 55%"),
        };

    [Fact]
    public void ParsesTranslucentInkLikeTheDoc()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);

        // "#1D1D1B · 6%" => alpha round(0.06 * 255) = 0x0F (the example in the design-system brief).
        Assert.Equal(Color.FromArgb(0x0F, 0x1D, 0x1D, 0x1B), ParseToken("#1D1D1B · 6%"));
        Assert.Equal(Color.FromArgb(0x4D, 0x8C, 0xA2, 0xFF), ParseToken("#8CA2FF · 30%"));
        Assert.Equal(Color.FromArgb(0xFF, 0xEF, 0xEF, 0xEC), ParseToken("#EFEFEC"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTokenHasExactColorAndFrozenBrush(bool dark)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            ResourceDictionary tokens = dark ? new TokensDark() : new TokensLight();
            Assert.Equal(dark ? "Dark" : "Light", tokens["ThemeName"]);
            foreach (var (name, values) in Expected)
            {
                var expected = ParseToken(dark ? values.Dark : values.Light);
                var color = Assert.IsType<Color>(tokens[name + "Color"]);
                Assert.True(expected == color, $"{name}Color is {color}, expected {expected} ({(dark ? "dark" : "light")})");
                var brush = Assert.IsType<SolidColorBrush>(tokens[name + "Brush"]);
                Assert.True(brush.IsFrozen, $"{name}Brush must be frozen");
                Assert.Equal(expected, brush.Color);
            }
        });
    }

    [Fact]
    public void BothThemesDefineTheSameKeys()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var light = new TokensLight().Keys.Cast<object>().Select(k => k.ToString()).Order().ToList();
            var dark = new TokensDark().Keys.Cast<object>().Select(k => k.ToString()).Order().ToList();
            Assert.Equal(light, dark);
            Assert.Equal((Expected.Count * 2) + 1, light.Count);
        });
    }

    /// <summary>"#RRGGBB" or "#RRGGBB · N%" (alpha = round(N% x 255), midpoint away from zero).</summary>
    internal static Color ParseToken(string spec)
    {
        var parts = spec.Split('·', StringSplitOptions.TrimEntries);
        var hex = parts[0].TrimStart('#');
        var r = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte a = 0xFF;
        if (parts.Length == 2)
        {
            var percent = decimal.Parse(parts[1].TrimEnd('%'), CultureInfo.InvariantCulture);
            a = (byte)Math.Round(percent / 100m * 255m, MidpointRounding.AwayFromZero);
        }

        return Color.FromArgb(a, r, g, b);
    }
}
