using System.Runtime.InteropServices;
using NimPad.Core.Domain;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>
/// The installed fixed-pitch font families, read once through GDI's <c>EnumFontFamiliesEx</c> (a few milliseconds;
/// the view models ask for the list on every settings change, so it is cached). Vertical (<c>@</c>) and symbol fonts
/// are skipped; <see cref="AppSettings.DefaultEditorFontFamily"/> is always included.
/// </summary>
public sealed class FontCatalog : IFontCatalog
{
    private const int FixedPitch = 1;
    private const byte DefaultCharset = 1;
    private const byte SymbolCharset = 2;

    private readonly Lazy<IReadOnlyList<string>> _families;

    /// <summary>Creates the catalog (nothing is enumerated until <see cref="MonospaceFamilies"/> is read).</summary>
    public FontCatalog()
    {
        _families = new Lazy<IReadOnlyList<string>>(Enumerate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private delegate int EnumFontFamExProc(ref LogFont font, IntPtr metrics, uint fontType, IntPtr lParam);

    /// <inheritdoc/>
    public IReadOnlyList<string> MonospaceFamilies => _families.Value;

    private static IReadOnlyList<string> Enumerate()
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase) { AppSettings.DefaultEditorFontFamily };
        var dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                var filter = new LogFont { lfCharSet = DefaultCharset };
                EnumFontFamExProc callback = (ref LogFont font, IntPtr _, uint _, IntPtr _) =>
                {
                    var name = font.lfFaceName;
                    if ((font.lfPitchAndFamily & 0x3) == FixedPitch && font.lfCharSet != SymbolCharset
                        && !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
                    {
                        names.Add(name);
                    }

                    return 1;
                };
                EnumFontFamiliesEx(dc, ref filter, callback, IntPtr.Zero, 0);
                GC.KeepAlive(callback);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            System.Diagnostics.Trace.TraceWarning($"Listing the installed fonts failed: {ex.Message}");
        }
        finally
        {
            if (dc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, dc);
            }
        }

        return [.. names];
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int EnumFontFamiliesEx(IntPtr dc, ref LogFont logFont, EnumFontFamExProc callback, IntPtr lParam, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }
}
