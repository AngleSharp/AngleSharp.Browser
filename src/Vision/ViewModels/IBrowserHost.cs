namespace Vision.ViewModels;

using System;
using AngleSharp.Browser;

/// <summary>
/// What a browser window needs from the application: other windows and the shared settings.
/// </summary>
public interface IBrowserHost
{
    /// <summary>
    /// Gets the color scheme of the browser ("light" or "dark"), which pages are told about.
    /// </summary>
    string ColorScheme { get; }

    /// <summary>
    /// Raised on the UI thread when <see cref="ColorScheme"/> changed.
    /// </summary>
    event EventHandler? ColorSchemeChanged;

    /// <summary>
    /// Opens a browser window, which shares the profile of the given engine, if possible.
    /// </summary>
    void OpenWindow(string? address, BrowserEngine? opener);

    void OpenSettings();

    void OpenAbout(BrowserEngine owner);

    /// <summary>
    /// Shows how the connection to the site of the engine's page is secured.
    /// </summary>
    void OpenSiteInfo(BrowserEngine owner);
}
