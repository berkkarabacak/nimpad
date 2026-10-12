using System.Diagnostics;
using System.IO;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>
/// Opens web links (<c>http</c>, <c>https</c>, <c>mailto</c>) in the default browser and <c>file:</c> folders in File
/// Explorer. Other schemes are refused, so a crafted link cannot start a program.
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The scheme is not allowed, or the file URI is not a folder.</exception>
    public void OpenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string target;
        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            target = uri.LocalPath;
            if (!Directory.Exists(target))
            {
                throw new ArgumentException($"{target} is not a folder.", nameof(uri));
            }
        }
        else if (uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            target = uri.AbsoluteUri;
        }
        else
        {
            throw new ArgumentException($"Links of the kind {uri.Scheme} can't be opened.", nameof(uri));
        }

        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
