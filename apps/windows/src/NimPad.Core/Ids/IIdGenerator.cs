namespace NimPad.Core.Ids;

/// <summary>Creates new identifiers. Services take one so tests can make IDs deterministic.</summary>
public interface IIdGenerator
{
    /// <summary>Returns a new UUID. Production implementations return version 7.</summary>
    Guid NewGuid();
}

/// <summary>Creates UUIDv7 values whose timestamp comes from a <see cref="TimeProvider"/>.</summary>
/// <param name="timeProvider">The clock embedded in each ID; <see cref="TimeProvider.System"/> by default.</param>
public sealed class UuidV7Generator(TimeProvider? timeProvider = null) : IIdGenerator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>A generator using the system clock.</summary>
    public static UuidV7Generator Default { get; } = new();

    /// <inheritdoc/>
    public Guid NewGuid() => Guid.CreateVersion7(_timeProvider.GetUtcNow());
}

/// <summary>Typed helpers over <see cref="IIdGenerator"/>.</summary>
public static class IdGeneratorExtensions
{
    /// <summary>Creates a new <see cref="NodeId"/>.</summary>
    public static NodeId NewNodeId(this IIdGenerator ids) => new(ids.NewGuid());

    /// <summary>Creates a new <see cref="WorkspaceId"/>.</summary>
    public static WorkspaceId NewWorkspaceId(this IIdGenerator ids) => new(ids.NewGuid());

    /// <summary>Creates a new <see cref="TrashBatchId"/>.</summary>
    public static TrashBatchId NewTrashBatchId(this IIdGenerator ids) => new(ids.NewGuid());
}
