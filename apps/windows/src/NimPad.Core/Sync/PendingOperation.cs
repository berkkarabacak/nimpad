using System.Text.Json;
using System.Text.Json.Nodes;
using NimPad.Core.Ids;
using NimPad.Core.Time;

namespace NimPad.Core.Sync;

/// <summary>
/// One row of <c>pending_operations</c>: a local mutation waiting to be pushed (architecture.md, "Pending operation
/// queue"). <see cref="ToContractJson"/> renders the part that is pushed, in the shape of the contracts'
/// <c>PendingOperation</c>.
/// </summary>
/// <param name="Seq">The push order.</param>
/// <param name="OpId">The idempotency key sent to the server (UUIDv7).</param>
/// <param name="WorkspaceId">The target workspace.</param>
/// <param name="NodeId">The target node (which may no longer exist locally, for a purge).</param>
/// <param name="Kind">The operation kind.</param>
/// <param name="PayloadJson">The payload as compact JSON (shapes: contracts' <c>PendingOperationPayload</c>).</param>
/// <param name="BaseMetaRev">The server metadata revision the change was made against; null when never synced.</param>
/// <param name="BaseContentRev">The server content revision the change was made against; null when never synced.</param>
/// <param name="DeviceId">This installation.</param>
/// <param name="ActorKind">Who caused the change.</param>
/// <param name="CreatedAt">When the operation was queued.</param>
/// <param name="Attempts">How many pushes were attempted (always 0 in Phase 1).</param>
/// <param name="LastError">The last push error, if any.</param>
public sealed record PendingOperation(
    long Seq,
    Guid OpId,
    WorkspaceId WorkspaceId,
    NodeId NodeId,
    PendingOperationKind Kind,
    string PayloadJson,
    long? BaseMetaRev,
    long? BaseContentRev,
    Guid DeviceId,
    LocalActorKind ActorKind,
    DateTimeOffset CreatedAt,
    int Attempts,
    string? LastError)
{
    /// <summary>The payload parsed as a JSON object.</summary>
    public JsonObject Payload => JsonNode.Parse(PayloadJson)?.AsObject() ?? [];

    /// <summary>
    /// The pushed part as camelCase JSON: <c>opId</c>, <c>workspaceId</c>, <c>nodeId</c>, <c>kind</c>,
    /// <c>payload</c>, <c>baseMetaRev</c>, <c>baseContentRev</c>, <c>deviceId</c>, <c>actorKind</c>,
    /// <c>createdAt</c>. The local bookkeeping (<see cref="Seq"/>, <see cref="Attempts"/>, <see cref="LastError"/>)
    /// is not part of the contract.
    /// </summary>
    public string ToContractJson()
    {
        var json = new JsonObject
        {
            ["opId"] = Uuid.Format(OpId),
            ["workspaceId"] = WorkspaceId.ToString(),
            ["nodeId"] = NodeId.ToString(),
            ["kind"] = Kind.ToWireName(),
            ["payload"] = Payload,
            ["baseMetaRev"] = BaseMetaRev,
            ["baseContentRev"] = BaseContentRev,
            ["deviceId"] = Uuid.Format(DeviceId),
            ["actorKind"] = ActorKind.ToWireName(),
            ["createdAt"] = Timestamps.ToText(CreatedAt),
        };
        return json.ToJsonString(PendingOperationPayloads.JsonOptions);
    }

    /// <summary>
    /// Parses the contract JSON produced by <see cref="ToContractJson"/> (for example a shared fixture). Shapes are
    /// not validated beyond what is needed to read the fields.
    /// </summary>
    /// <exception cref="FormatException">A field is missing or has the wrong type.</exception>
    public static PendingOperation FromContractJson(string json, long seq = 0)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Expected a JSON object.");
            return new PendingOperation(
                seq,
                Uuid.Parse(Required(root, "opId").GetValue<string>(), "opId"),
                WorkspaceId.Parse(Required(root, "workspaceId").GetValue<string>()),
                NodeId.Parse(Required(root, "nodeId").GetValue<string>()),
                PendingOperationNames.ParseKind(Required(root, "kind").GetValue<string>()),
                Required(root, "payload").AsObject().ToJsonString(PendingOperationPayloads.JsonOptions),
                root["baseMetaRev"]?.GetValue<long>(),
                root["baseContentRev"]?.GetValue<long>(),
                Uuid.Parse(Required(root, "deviceId").GetValue<string>(), "deviceId"),
                PendingOperationNames.ParseActor(Required(root, "actorKind").GetValue<string>()),
                Timestamps.Parse(Required(root, "createdAt").GetValue<string>()),
                0,
                null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException($"Not a pending operation: {ex.Message}", ex);
        }
    }

    private static JsonNode Required(JsonObject root, string name) =>
        root[name] ?? throw new FormatException($"Missing '{name}'.");
}
