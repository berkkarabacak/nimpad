using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>The stored text of a managed document.</summary>
/// <param name="NodeId">The document.</param>
/// <param name="Text">The canonical text (LF line endings).</param>
/// <param name="Format">The document format.</param>
/// <param name="Checksum">The content checksum of <paramref name="Text"/> (<c>sha256:</c> + hex).</param>
/// <param name="UpdatedAt">The last change of the text or format (UTC, millisecond precision).</param>
/// <param name="Version">
/// A local counter that every committed change of the text or format increments (1 for a new document). It lets an
/// editor tell whether the stored content moved on since it loaded it.
/// </param>
public sealed record DocumentContent(
    NodeId NodeId,
    string Text,
    DocumentFormat Format,
    string Checksum,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>The outcome of saving a document's text.</summary>
/// <param name="NodeId">The document.</param>
/// <param name="Changed">
/// <see langword="false"/> when the canonical text was identical to the stored text (same checksum) and nothing was
/// written.
/// </param>
/// <param name="Checksum">The checksum of the stored text.</param>
/// <param name="Version">The stored content version (see <see cref="DocumentContent.Version"/>).</param>
/// <param name="UpdatedAt">When the stored content last changed.</param>
public sealed record DocumentSaveResult(NodeId NodeId, bool Changed, string Checksum, long Version, DateTimeOffset UpdatedAt);
