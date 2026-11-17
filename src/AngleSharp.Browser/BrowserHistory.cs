namespace AngleSharp.Browser;

using System.Collections.Generic;

/// <summary>
/// A copy of the session history of an engine (see <see cref="BrowserEngine.History"/>).
/// </summary>
/// <param name="Entries">The entries, the oldest first.</param>
/// <param name="Index">The index of the current entry, or -1 if there is none.</param>
public sealed record BrowserHistory(IReadOnlyList<BrowserHistoryEntry> Entries, int Index)
{
    /// <summary>
    /// Gets the entries before the current one, the closest first (one step back, two, ...).
    /// </summary>
    public IReadOnlyList<BrowserHistoryEntry> Back
    {
        get
        {
            var back = new List<BrowserHistoryEntry>();

            for (var i = Index - 1; i >= 0; i--)
            {
                back.Add(Entries[i]);
            }

            return back;
        }
    }

    /// <summary>
    /// Gets the entries after the current one, the closest first (one step forward, two, ...).
    /// </summary>
    public IReadOnlyList<BrowserHistoryEntry> Forward
    {
        get
        {
            var forward = new List<BrowserHistoryEntry>();

            for (var i = Index + 1; i < Entries.Count; i++)
            {
                forward.Add(Entries[i]);
            }

            return forward;
        }
    }
}

/// <summary>
/// An entry of the session history.
/// </summary>
/// <param name="Url">The address of the entry.</param>
/// <param name="Title">The title of its document, or empty if it has none (yet).</param>
public sealed record BrowserHistoryEntry(string Url, string Title);
