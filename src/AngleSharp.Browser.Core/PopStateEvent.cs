namespace AngleSharp.Browser;

using AngleSharp.Attributes;
using AngleSharp.Dom.Events;

/// <summary>
/// Fired at the window when the history moves to an entry of the same document, carrying the
/// state a script gave that entry with <c>pushState</c> or <c>replaceState</c>.
/// </summary>
[DomName("PopStateEvent")]
public sealed class PopStateEvent : Event
{
    /// <summary>
    /// Creates a new popstate event.
    /// </summary>
    /// <param name="state">The state of the entry moved to.</param>
    public PopStateEvent(object? state)
        : base("popstate")
    {
        State = state;
    }

    /// <summary>
    /// Gets the state of the entry moved to.
    /// </summary>
    [DomName("state")]
    public object? State { get; }
}
