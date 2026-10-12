using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Ordering;
using NimPad.Core.Time;

namespace NimPad.Core.Sync;

/// <summary>
/// Checks a pending operation, as contract JSON (<see cref="PendingOperation.ToContractJson"/>), against the
/// contracts' <c>PendingOperation</c> and <c>PushOperation</c> schemas
/// (<c>packages/contracts/src/pending-operations.ts</c>): strict objects, lowercase hyphenated IDs (version 7 where
/// NimPad generates them), millisecond UTC timestamps, positive revisions, stored-form names, valid position keys and
/// the per-kind payload shapes. The shared fixture <c>pending-operations.json</c> pins the rules.
/// </summary>
/// <remarks>
/// The queue runs every operation it writes through <see cref="Validate"/> in Debug builds, so an operation the server
/// would reject fails the local write in tests instead of poisoning the queue.
/// </remarks>
internal static partial class PendingOperationContract
{
    private static readonly string[] OperationKeys =
        ["opId", "workspaceId", "nodeId", "kind", "payload", "baseMetaRev", "baseContentRev", "deviceId", "actorKind", "createdAt"];

    private static readonly string[] FolderCreateKeys = ["nodeKind", "parentId", "name", "position"];
    private static readonly string[] DocumentCreateKeys = ["nodeKind", "parentId", "name", "position", "format", "checksum"];

    /// <summary>
    /// The contract violations of <paramref name="json"/>; empty when it is a valid <c>PendingOperation</c> (and, with
    /// <paramref name="forPush"/>, also a valid <c>PushOperation</c>: rebased base revisions).
    /// </summary>
    public static IReadOnlyList<string> Validate(string json, bool forPush = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"not JSON: {ex.Message}"];
        }

        if (parsed is not JsonObject operation)
        {
            return ["not a JSON object"];
        }

        var errors = new List<string>();
        RequireExactKeys(operation, OperationKeys, string.Empty, errors);
        var opId = Text(operation, "opId");
        if (!Uuid.IsCanonicalV7(opId))
        {
            errors.Add("opId: expected a lowercase version 7 UUID");
        }

        CheckUuid(operation, "workspaceId", nullable: false, errors);
        var nodeId = CheckUuid(operation, "nodeId", nullable: false, errors);
        if (!Uuid.IsCanonicalV7(Text(operation, "deviceId")))
        {
            errors.Add("deviceId: expected a lowercase version 7 UUID");
        }

        if (Text(operation, "actorKind") is not ("user" or "import" or "system"))
        {
            errors.Add("actorKind: expected user, import or system");
        }

        CheckTimestamp(operation, "createdAt", errors);
        var baseMetaRev = CheckRevision(operation, "baseMetaRev", errors);
        var baseContentRev = CheckRevision(operation, "baseContentRev", errors);

        PendingOperationKind? kind = null;
        try
        {
            kind = Text(operation, "kind") is { } name ? PendingOperationNames.ParseKind(name) : null;
        }
        catch (FormatException)
        {
            // Reported below.
        }

        if (kind is not { } known)
        {
            errors.Add("kind: unknown operation kind");
            return errors;
        }

        if (operation["payload"] is not JsonObject payload)
        {
            errors.Add("payload: expected an object");
            return errors;
        }

        CheckPayload(known, payload, nodeId, errors);
        if (known == PendingOperationKind.Create)
        {
            if (!Uuid.IsCanonicalV7(nodeId))
            {
                errors.Add("nodeId: a new node ID must be a version 7 UUID");
            }

            if (baseMetaRev is not null || baseContentRev is not null)
            {
                errors.Add("baseMetaRev: a create has no server base revisions");
            }
        }

        if (forPush && known != PendingOperationKind.Create && baseMetaRev is null)
        {
            errors.Add("baseMetaRev: rebase before pushing (only a create may have a null base revision)");
        }

        if (forPush && known == PendingOperationKind.UpdateContent && baseContentRev is null)
        {
            errors.Add("baseContentRev: rebase before pushing (an update_content needs the content revision)");
        }

        return errors;
    }

    private static void CheckPayload(PendingOperationKind kind, JsonObject payload, string? nodeId, List<string> errors)
    {
        switch (kind)
        {
            case PendingOperationKind.Create:
                var nodeKind = Text(payload, "nodeKind");
                if (nodeKind is not ("folder" or "document"))
                {
                    errors.Add("payload.nodeKind: expected folder or document");
                    return;
                }

                RequireExactKeys(payload, nodeKind == "folder" ? FolderCreateKeys : DocumentCreateKeys, "payload.", errors);
                CheckParent(payload, nodeId, errors);
                CheckName(payload, errors);
                CheckPosition(payload, "position", errors);
                if (nodeKind == "document")
                {
                    CheckFormat(payload, errors);
                    CheckChecksum(payload, errors);
                }

                break;
            case PendingOperationKind.UpdateContent:
                RequireExactKeys(payload, ["checksum", "format"], "payload.", errors);
                CheckChecksum(payload, errors);
                CheckFormat(payload, errors);
                break;
            case PendingOperationKind.Rename:
                RequireExactKeys(payload, ["name"], "payload.", errors);
                CheckName(payload, errors);
                break;
            case PendingOperationKind.Move:
                RequireExactKeys(payload, ["parentId", "position"], "payload.", errors);
                CheckParent(payload, nodeId, errors);
                CheckPosition(payload, "position", errors);
                break;
            case PendingOperationKind.Pin:
                RequireExactKeys(payload, ["pinned", "pinPosition"], "payload.", errors);
                var pinned = payload["pinned"] is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? flag.GetValue<bool>()
                    : (bool?)null;
                if (pinned is null)
                {
                    errors.Add("payload.pinned: expected a boolean");
                }
                else if (pinned.Value)
                {
                    CheckPosition(payload, "pinPosition", errors);
                }
                else if (!IsNull(payload, "pinPosition"))
                {
                    errors.Add("payload.pinPosition: must be null when unpinned");
                }

                break;
            case PendingOperationKind.Delete:
                RequireExactKeys(payload, ["trashBatchId", "deletedAt"], "payload.", errors);
                if (!Uuid.IsCanonicalV7(Text(payload, "trashBatchId")))
                {
                    errors.Add("payload.trashBatchId: expected a lowercase version 7 UUID");
                }

                CheckTimestamp(payload, "deletedAt", errors, "payload.");
                break;
            case PendingOperationKind.Restore:
                RequireExactKeys(payload, ["trashBatchId"], "payload.", errors);
                CheckUuid(payload, "trashBatchId", nullable: false, errors, "payload.");
                break;
            case PendingOperationKind.Purge:
                RequireExactKeys(payload, [], "payload.", errors);
                break;
            default:
                errors.Add("kind: unknown operation kind");
                break;
        }
    }

    private static void RequireExactKeys(JsonObject value, IReadOnlyCollection<string> keys, string path, List<string> errors)
    {
        foreach (var (key, _) in value)
        {
            if (!keys.Contains(key))
            {
                errors.Add($"{path}{key}: unknown key");
            }
        }

        foreach (var key in keys)
        {
            if (!value.ContainsKey(key))
            {
                errors.Add($"{path}{key}: missing");
            }
        }
    }

    private static string? Text(JsonObject value, string key) =>
        value[key] is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;

    private static bool IsNull(JsonObject value, string key) => value.ContainsKey(key) && value[key] is null;

    private static string? CheckUuid(JsonObject value, string key, bool nullable, List<string> errors, string path = "")
    {
        if (nullable && IsNull(value, key))
        {
            return null;
        }

        var text = Text(value, key);
        if (text is null || !Uuid.IsHyphenated(text, lowercaseOnly: true))
        {
            errors.Add($"{path}{key}: expected a lowercase hyphenated UUID");
            return null;
        }

        return text;
    }

    private static void CheckParent(JsonObject payload, string? nodeId, List<string> errors)
    {
        var parentId = CheckUuid(payload, "parentId", nullable: true, errors, "payload.");
        if (parentId is not null && parentId == nodeId)
        {
            errors.Add("payload.parentId: a node cannot be its own parent");
        }
    }

    private static void CheckName(JsonObject payload, List<string> errors)
    {
        var name = Text(payload, "name");
        if (name is null || !NodeNames.IsStoredForm(name))
        {
            errors.Add("payload.name: expected a trimmed name of 1-255 characters without control characters");
        }
    }

    private static void CheckPosition(JsonObject payload, string key, List<string> errors)
    {
        if (!FractionalIndex.IsValidKey(Text(payload, key)))
        {
            errors.Add($"payload.{key}: expected a fractional-index position key");
        }
    }

    private static void CheckFormat(JsonObject payload, List<string> errors)
    {
        if (Text(payload, "format") is not { } format || !DocumentFormats.TryParse(format, out _))
        {
            errors.Add("payload.format: unknown format");
        }
    }

    private static void CheckChecksum(JsonObject payload, List<string> errors)
    {
        if (Text(payload, "checksum") is not { } checksum || !ChecksumPattern().IsMatch(checksum))
        {
            errors.Add("payload.checksum: expected sha256: followed by 64 lowercase hex digits");
        }
    }

    private static void CheckTimestamp(JsonObject value, string key, List<string> errors, string path = "")
    {
        var text = Text(value, key);
        if (text is null || !Timestamps.TryParse(text, out var parsed) || Timestamps.ToText(parsed) != text)
        {
            errors.Add($"{path}{key}: expected a UTC timestamp like 2026-10-12T13:59:43.123Z");
        }
    }

    private static long? CheckRevision(JsonObject value, string key, List<string> errors)
    {
        if (IsNull(value, key))
        {
            return null;
        }

        if (value[key] is JsonValue number && number.GetValueKind() == JsonValueKind.Number
            && number.TryGetValue<long>(out var revision) && revision >= 1)
        {
            return revision;
        }

        errors.Add($"{key}: expected null or an integer revision of at least 1");
        return null;
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();
}
