namespace AngleSharp.Browser;

using System;

/// <summary>
/// Elements the page shows fullscreen (Fullscreen API, see <see cref="Fullscreen"/>): they cover
/// the viewport, the host is told to show the page fullscreen, and Esc ends it.
/// </summary>
partial class BrowserEngine
{
    private readonly Fullscreen _fullscreen;
    private volatile bool _isFullscreen;

    /// <summary>
    /// The page started or ended showing an element fullscreen (or another one), which is laid
    /// out anew. Requires exclusive access.
    /// </summary>
    private void OnFullscreenChanged(bool isFullscreen)
    {
        if (_isDisposed)
        {
            return;
        }

        var wasFullscreen = _isFullscreen;
        _isFullscreen = isFullscreen;
        Invalidate();

        if (wasFullscreen != isFullscreen)
        {
            FullscreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Ends showing an element fullscreen on Esc. Requires exclusive access.
    /// </summary>
    private bool ExitFullscreenOnEscape() => _isFullscreen && _fullscreen.ExitAll(_document);
}
