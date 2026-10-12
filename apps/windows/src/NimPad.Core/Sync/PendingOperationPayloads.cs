using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Time;

namespace NimPad.Core.Sync;

/// <summary>
/// Builds the payload JSON of each operation kind exactly as the contracts define it
/// (<c>packages/contracts/src/pending-operations.ts</c>; strict objects, camelCase).
/// </summary>
internal static class PendingOperationPayloads
{
    /// <summary>Compact output that keeps non-ASCII text readable (payloads are stored, never embedded in HTML).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// <c>create</c>: <c>{ nodeKind, parentId, name, position }</c>, plus <c>format</c> and <c>checksum</c> for a
    /// document.
    /// </summary>
    public static string Create(NodeKind kind, NodeId? parentId, string name, string position, DocumentFormat? format, string? checksum)
    {
        var payload = new JsonObject
        {
            ["nodeKind"] = kind.ToWireName(),
            ["parentId"] = parentId?.ToString(),
            ["name"] = name,
            ["position"] = position,
        };
        if (kind == NodeKind.Document)
        {
            payload["format"] = (format ?? throw new ArgumentNullException(nameof(format))).ToWireName();
            payload["checksum"] = checksum ?? throw new ArgumentNullException(nameof(checksum));
        }

        return Serialize(payload);
    }

    /// <summary><c>update_content</c>: <c>{ checksum, format }</c>.</summary>
    public static string UpdateContent(string checksum, DocumentFormat format) =>
        Serialize(new JsonObject { ["checksum"] = checksum, ["format"] = format.ToWireName() });

    /// <summary><c>rename</c>: <c>{ name }</c>.</summary>
    public static string Rename(string name) => Serialize(new JsonObject { ["name"] = name });

    /// <summary><c>move</c>: <c>{ parentId, position }</c>; a null parent is the workspace root.</summary>
    public static string Move(NodeId? parentId, string position) =>
        Serialize(new JsonObject { ["parentId"] = parentId?.ToString(), ["position"] = position });

    /// <summary><c>pin</c>: <c>{ pinned: true, pinPosition }</c> or <c>{ pinned: false, pinPosition: null }</c>.</summary>
    public static string Pin(bool pinned, string? pinPosition)
    {
        if (pinned != (pinPosition is not null))
        {
            throw new ArgumentException("pinPosition is set exactly when pinned.", nameof(pinPosition));
        }

        return Serialize(new JsonObject { ["pinned"] = pinned, ["pinPosition"] = pinPosition });
    }

    /// <summary><c>delete</c>: <c>{ trashBatchId, deletedAt }</c>.</summary>
    public static string Delete(TrashBatchId batchId, DateTimeOffset deletedAt) =>
        Serialize(new JsonObject { ["trashBatchId"] = batchId.ToString(), ["deletedAt"] = Timestamps.ToText(deletedAt) });

    /// <summary><c>restore</c>: <c>{ trashBatchId }</c>.</summary>
    public static string Restore(TrashBatchId batchId) =>
        Serialize(new JsonObject { ["trashBatchId"] = batchId.ToString() });

    /// <summary><c>purge</c>: <c>{}</c>.</summary>
    public static string Purge() => "{}";

    /// <summary>Returns <paramref name="payloadJson"/> with the given properties replaced (others keep their order).</summary>
    public static string With(string payloadJson, params ReadOnlySpan<(string Name, JsonNode? Value)> changes)
    {
        var payload = JsonNode.Parse(payloadJson)?.AsObject()
            ?? throw new InvalidOperationException("A pending operation payload is not a JSON object.");
        foreach (var (name, value) in changes)
        {
            payload[name] = value;
        }

        return Serialize(payload);
    }

    private static string Serialize(JsonObject payload) => payload.ToJsonString(JsonOptions);
}
