using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NimPad.Core.Storage;

namespace NimPad.Presentation.Integration;

/// <summary>What a second launch of NimPad forwards to the running instance.</summary>
/// <param name="Files">Full paths of the files to open as external tabs (none: just bring NimPad forward).</param>
/// <param name="Activate">
/// Whether the running window comes to the front; <see langword="false"/> for a <c>--background</c> launch (the
/// start-with-Windows entry while NimPad already runs).
/// </param>
public sealed record SingleInstanceMessage(IReadOnlyList<string> Files, bool Activate = true)
{
    /// <summary>Value equality including the file list.</summary>
    public bool Equals(SingleInstanceMessage? other) =>
        other is not null && Activate == other.Activate && Files.SequenceEqual(other.Files, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Activate, Files.Count);
}

/// <summary>
/// The single-instance wire format and names. A second launch connects to the running instance's named pipe, writes
/// one message (a 4-byte little-endian length, then UTF-8 JSON <c>{"version":1,"files":[…],"activate":true}</c>) and
/// waits for the one-byte <see cref="Acknowledgement"/>. Pure, so it is testable on any OS; the App's
/// <c>SingleInstance</c> owns the mutex, the pipe and its access control.
/// </summary>
public static class SingleInstanceProtocol
{
    /// <summary>The message format version.</summary>
    public const int Version = 1;

    /// <summary>The largest accepted message (a Windows command line is at most 32,767 characters).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>The most files one message may carry.</summary>
    public const int MaxFiles = 4096;

    /// <summary>The longest accepted path.</summary>
    public const int MaxPathLength = 32_767;

    /// <summary>The byte the running instance answers with once it has taken the message.</summary>
    public const byte Acknowledgement = 0x06;

    /// <summary>The name prefix of the pipe and the mutex.</summary>
    public const string NamePrefix = "NimPad-";

    /// <summary>The message as UTF-8 JSON (without the length prefix).</summary>
    public static byte[] Encode(SingleInstanceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message.Files);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("files");
            foreach (var file in message.Files)
            {
                writer.WriteStringValue(file);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("activate", message.Activate);
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > MaxMessageBytes)
        {
            throw new ArgumentException($"The message is larger than {MaxMessageBytes} bytes.", nameof(message));
        }

        return bytes;
    }

    /// <summary>Reads a message encoded by <see cref="Encode"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid message.</exception>
    public static SingleInstanceMessage Decode(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"The message is larger than {MaxMessageBytes} bytes.");
        }

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The message is not a JSON object.");
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != Version)
            {
                throw new InvalidDataException("The message has an unsupported version.");
            }

            var files = new List<string>();
            if (root.TryGetProperty("files", out var array))
            {
                if (array.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("\"files\" is not an array.");
                }

                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("A file is not a string.");
                    }

                    files.Add(item.GetString()!);
                }
            }

            var activate = true;
            if (root.TryGetProperty("activate", out var flag))
            {
                activate = flag.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new InvalidDataException("\"activate\" is not a boolean."),
                };
            }

            try
            {
                Validate(files);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }

            return new SingleInstanceMessage(files, activate);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The message is not valid JSON.", ex);
        }
    }

    /// <summary>Writes the length-prefixed message and flushes.</summary>
    public static async Task WriteAsync(Stream stream, SingleInstanceMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = Encode(message);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one length-prefixed message.</summary>
    /// <exception cref="InvalidDataException">The stream ended early or the message is invalid or too large.</exception>
    public static async Task<SingleInstanceMessage> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"The message length {length} is out of range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return Decode(payload);
    }

    /// <summary>
    /// The pipe name shared by every launch over the same data: <c>NimPad-&lt;user SID&gt;</c> for the default profile,
    /// with <c>-&lt;16 hex digits&gt;</c> of the data root's hash appended for a portable or <c>--data-dir</c>
    /// profile, so launches over different data never forward to each other. The mutex is
    /// <c>Local\</c> + the same name.
    /// </summary>
    /// <param name="userSid">The current user's SID, for example <c>S-1-5-21-…</c>.</param>
    /// <param name="paths">Where this launch keeps its data.</param>
    public static string GetInstanceName(string userSid, AppPaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentNullException.ThrowIfNull(paths);
        var builder = new StringBuilder(NamePrefix);
        foreach (var c in userSid.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
        }

        if (paths.Source != DataRootSource.LocalAppData)
        {
            var root = paths.DataRoot.TrimEnd('\\', '/').ToUpperInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));
            builder.Append('-').Append(Convert.ToHexStringLower(hash, 0, 8));
        }

        return builder.ToString();
    }

    private static void Validate(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > MaxFiles)
        {
            throw new ArgumentException($"A message carries at most {MaxFiles} files.", nameof(files));
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || file.Length > MaxPathLength || file.Contains('\0', StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(file))
            {
                throw new ArgumentException("Every file must be a full path.", nameof(files));
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The message ended early.", ex);
        }
    }
}
