namespace NimPad.Core.Sync;

/// <summary>The kinds of queued local mutations (contracts: <c>PendingOperationKind</c>).</summary>
public enum PendingOperationKind
{
    /// <summary><c>create</c>: a new folder or document with its full initial state.</summary>
    Create,

    /// <summary><c>update_content</c>: a document's text and/or format changed (payload carries only the checksum).</summary>
    UpdateContent,

    /// <summary><c>rename</c>.</summary>
    Rename,

    /// <summary><c>move</c>: a new parent and/or position.</summary>
    Move,

    /// <summary><c>pin</c>: pinned with a position, or unpinned.</summary>
    Pin,

    /// <summary><c>delete</c>: the node's subtree moved to the Trash as one batch.</summary>
    Delete,

    /// <summary><c>restore</c>: a trashed batch brought back.</summary>
    Restore,

    /// <summary><c>purge</c>: deleted forever.</summary>
    Purge,
}

/// <summary>Who caused a local operation (contracts: <c>LocalActorKind</c>). Agents act only through the server.</summary>
public enum LocalActorKind
{
    /// <summary><c>user</c>: a person using this app.</summary>
    User,

    /// <summary><c>import</c>: content copied in from a file.</summary>
    Import,

    /// <summary><c>system</c>: automatic maintenance, such as the 30-day Trash cleanup.</summary>
    System,
}

/// <summary>Wire names for <see cref="PendingOperationKind"/> and <see cref="LocalActorKind"/>.</summary>
public static class PendingOperationNames
{
    /// <summary>The wire name, e.g. <c>update_content</c>.</summary>
    public static string ToWireName(this PendingOperationKind kind) => kind switch
    {
        PendingOperationKind.Create => "create",
        PendingOperationKind.UpdateContent => "update_content",
        PendingOperationKind.Rename => "rename",
        PendingOperationKind.Move => "move",
        PendingOperationKind.Pin => "pin",
        PendingOperationKind.Delete => "delete",
        PendingOperationKind.Restore => "restore",
        PendingOperationKind.Purge => "purge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown operation kind."),
    };

    /// <summary>Parses an operation kind's wire name.</summary>
    /// <exception cref="FormatException">The name is unknown.</exception>
    public static PendingOperationKind ParseKind(string name) => name switch
    {
        "create" => PendingOperationKind.Create,
        "update_content" => PendingOperationKind.UpdateContent,
        "rename" => PendingOperationKind.Rename,
        "move" => PendingOperationKind.Move,
        "pin" => PendingOperationKind.Pin,
        "delete" => PendingOperationKind.Delete,
        "restore" => PendingOperationKind.Restore,
        "purge" => PendingOperationKind.Purge,
        _ => throw new FormatException($"Unknown operation kind '{name}'."),
    };

    /// <summary>The wire name: <c>user</c>, <c>import</c> or <c>system</c>.</summary>
    public static string ToWireName(this LocalActorKind actor) => actor switch
    {
        LocalActorKind.User => "user",
        LocalActorKind.Import => "import",
        LocalActorKind.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(actor), actor, "Unknown actor kind."),
    };

    /// <summary>Parses an actor kind's wire name.</summary>
    /// <exception cref="FormatException">The name is unknown (including <c>agent</c>, which is never local).</exception>
    public static LocalActorKind ParseActor(string name) => name switch
    {
        "user" => LocalActorKind.User,
        "import" => LocalActorKind.Import,
        "system" => LocalActorKind.System,
        _ => throw new FormatException($"Unknown local actor kind '{name}'."),
    };
}
