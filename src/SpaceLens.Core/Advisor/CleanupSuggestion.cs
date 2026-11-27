namespace SpaceLens.Core.Advisor;

public enum RiskLevel
{
    /// <summary>Caches and temporary data that apps rebuild automatically.</summary>
    Safe,
    /// <summary>Your own files or rebuildable project data: look before deleting.</summary>
    Review,
    /// <summary>System-level change with side effects (e.g. turning off hibernation).</summary>
    Advanced,
}

public enum SuggestionKind
{
    /// <summary>Delete everything inside the listed folders (permanently, skipping files in use).</summary>
    CleanFolderContents,
    /// <summary>Move the selected items to the Recycle Bin.</summary>
    RecycleItems,
    /// <summary>Empty the Recycle Bin.</summary>
    EmptyRecycleBin,
    /// <summary>Run a command (in a visible PowerShell window, optionally elevated).</summary>
    RunCommand,
    /// <summary>Nothing to automate: open the location / settings page so the user can act.</summary>
    OpenLocation,
}

public sealed class SuggestionItem
{
    public required string Path { get; init; }
    public long Bytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string? Note { get; init; }
    /// <summary>Whether the item starts out selected for cleanup.</summary>
    public bool Recommended { get; init; } = true;
}

public sealed class CleanupSuggestion
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public RiskLevel Risk { get; init; }
    public SuggestionKind Kind { get; init; }
    public long Bytes { get; init; }
    public IReadOnlyList<SuggestionItem> Items { get; init; } = [];
    /// <summary>Command for <see cref="SuggestionKind.RunCommand"/>, or a path/URI for <see cref="SuggestionKind.OpenLocation"/>.</summary>
    public string? Command { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>True when <see cref="Bytes"/> is an upper bound rather than what cleaning will free.</summary>
    public bool IsEstimate { get; init; }
    /// <summary>Short label for the primary action button.</summary>
    public string ActionLabel => Kind switch
    {
        SuggestionKind.CleanFolderContents => "Clean up",
        SuggestionKind.RecycleItems => "Move selected to Recycle Bin",
        SuggestionKind.EmptyRecycleBin => "Empty Recycle Bin",
        SuggestionKind.RunCommand => RequiresAdmin ? "Run (admin)" : "Run",
        _ => "Open",
    };
}

public sealed record CleanupOutcome(long BytesFreed, int ItemsRemoved, int Failures, string Message);
