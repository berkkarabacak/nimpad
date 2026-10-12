using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>A workspace: the root of one folder tree. Phase 1 has a single "My Workspace".</summary>
/// <param name="Id">The workspace's stable ID.</param>
/// <param name="Name">The display name.</param>
/// <param name="CreatedAt">When the workspace was created (UTC).</param>
/// <param name="UpdatedAt">The last change to the workspace record itself.</param>
public sealed record Workspace(WorkspaceId Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
