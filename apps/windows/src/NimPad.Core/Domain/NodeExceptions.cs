using NimPad.Core.Ids;

namespace NimPad.Core.Domain;

/// <summary>Base class for a tree operation that cannot be applied; the message is user-facing.</summary>
public class NodeOperationException : InvalidOperationException
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public NodeOperationException(string message)
        : base(message)
    {
    }
}

/// <summary>The node does not exist (or not in the expected state, for example it is already in the Trash).</summary>
public sealed class NodeNotFoundException : NodeOperationException
{
    /// <summary>Creates the exception for <paramref name="nodeId"/>.</summary>
    public NodeNotFoundException(NodeId nodeId, string? detail = null)
        : base(detail ?? $"The note or folder {nodeId} no longer exists.")
    {
        NodeId = nodeId;
    }

    /// <summary>The missing node.</summary>
    public NodeId NodeId { get; }
}

/// <summary>A move or copy would put a folder inside itself or one of its descendants.</summary>
public sealed class NodeCycleException : NodeOperationException
{
    /// <summary>Creates the exception for moving <paramref name="nodeId"/> under <paramref name="targetParentId"/>.</summary>
    public NodeCycleException(NodeId nodeId, NodeId targetParentId)
        : base("A folder can't be moved or copied into itself or one of its subfolders.")
    {
        NodeId = nodeId;
        TargetParentId = targetParentId;
    }

    /// <summary>The folder being moved or copied.</summary>
    public NodeId NodeId { get; }

    /// <summary>The requested destination, which is the folder itself or one of its descendants.</summary>
    public NodeId TargetParentId { get; }
}

/// <summary>A node name failed validation (see <see cref="NodeNames.Normalize"/>).</summary>
public sealed class InvalidNodeNameException : ArgumentException
{
    /// <summary>Creates the exception for <paramref name="name"/> with a user-facing <paramref name="reason"/>.</summary>
    public InvalidNodeNameException(string? name, string reason)
        : base(reason)
    {
        Name = name;
        Reason = reason;
    }

    /// <summary>The rejected name as given.</summary>
    public string? Name { get; }

    /// <summary>Why it was rejected, suitable for showing to the user.</summary>
    public string Reason { get; }
}

/// <summary>
/// A save was based on text that is no longer the stored text: something else (another device's change applied by
/// sync, an agent, another window) changed the document after the editor loaded it. Nothing was written; neither
/// version is lost (spec §4: never silently overwrite either version).
/// </summary>
public sealed class DocumentConflictException : NodeOperationException
{
    /// <summary>Creates the exception for <paramref name="nodeId"/>.</summary>
    public DocumentConflictException(NodeId nodeId, string expectedChecksum, string storedChecksum)
        : base("The note was changed somewhere else since it was opened, so your edits were not saved over it.")
    {
        NodeId = nodeId;
        ExpectedChecksum = expectedChecksum;
        StoredChecksum = storedChecksum;
    }

    /// <summary>The document.</summary>
    public NodeId NodeId { get; }

    /// <summary>The checksum of the stored text the save was based on.</summary>
    public string ExpectedChecksum { get; }

    /// <summary>The checksum of the text that is stored now.</summary>
    public string StoredChecksum { get; }
}
