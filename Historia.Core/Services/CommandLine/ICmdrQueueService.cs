namespace Historia.Services.CommandLine;

/// <summary>
/// The persisted list of navigations agents queued for the user (the sidebar's CMDR list).
/// Agents can only add to it; the user opens, marks and removes items. Nothing here touches a
/// repository.
/// </summary>
public interface ICmdrQueueService
{
    /// <summary>A snapshot of the items in the order they were queued, oldest first.</summary>
    IReadOnlyList<CmdrQueueItem> Items { get; }

    /// <summary>The number of items the user has not opened yet.</summary>
    int UnseenCount { get; }

    /// <summary>Raised on the UI thread after any change to the list.</summary>
    event EventHandler? Changed;

    /// <summary>Appends <paramref name="item"/>, dropping the oldest items beyond the cap.</summary>
    void Enqueue(CmdrQueueItem item);

    /// <summary>Marks the item with <paramref name="id"/> seen; an unknown id is ignored.</summary>
    void MarkSeen(string id);

    /// <summary>Removes the item with <paramref name="id"/>; an unknown id is ignored.</summary>
    void Remove(string id);
}
