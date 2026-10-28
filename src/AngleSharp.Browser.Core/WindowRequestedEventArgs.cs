namespace AngleSharp.Browser;

using System;

/// <summary>
/// The data of a page asking for a new window, e.g., by a link with target="_blank".
/// </summary>
/// <param name="url">The address to show in the new window.</param>
public sealed class WindowRequestedEventArgs(string url) : EventArgs
{
    /// <summary>
    /// Gets the address to show in the new window.
    /// </summary>
    public string Url { get; } = url;

    /// <summary>
    /// Gets or sets if the host opened the window. If not, the current page navigates instead.
    /// Must be set while handling the event.
    /// </summary>
    public bool Handled { get; set; }
}
