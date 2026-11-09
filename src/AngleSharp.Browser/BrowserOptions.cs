namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Net;
using AngleSharp.Io;
using AngleSharp.Io.Dom;
using AngleSharp.Io.ServiceWorkerApi;

/// <summary>
/// Options for creating a <see cref="BrowserEngine"/>. Everything that
/// touches the outside world (persistence, clipboard, user agent, ...) is
/// configured from here.
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>
    /// Gets or sets the directory where cookies, the web storage and the
    /// HTTP cache are persisted. If null, all data is kept in memory only.
    /// </summary>
    public string? ProfileDirectory { get; set; }

    /// <summary>
    /// Gets or sets the user agent to report. If null, the default of
    /// AngleSharp.Io is used.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets the headers added to every request, unless the request sets them
    /// already (e.g., Accept-Language or DNT).
    /// </summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the color scheme reported to prefers-color-scheme, e.g.,
    /// "light" or "dark". If null, queries for the preference never match.
    /// </summary>
    public string? ColorScheme { get; set; }

    /// <summary>
    /// Gets or sets if JavaScript should be evaluated.
    /// </summary>
    public bool IsScriptingEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the clipboard integration exposed to the page. If null,
    /// the clipboard API is not available.
    /// </summary>
    public IClipboardPlatform? ClipboardPlatform { get; set; }

    /// <summary>
    /// Gets or sets the initial viewport width in CSS pixels.
    /// </summary>
    public int ViewportWidth { get; set; } = 1280;

    /// <summary>
    /// Gets or sets the initial viewport height in CSS pixels.
    /// </summary>
    public int ViewportHeight { get; set; } = 800;

    /// <summary>
    /// Gets or sets the screen width reported to media queries.
    /// </summary>
    public int ScreenWidth { get; set; } = 1920;

    /// <summary>
    /// Gets or sets the screen height reported to media queries.
    /// </summary>
    public int ScreenHeight { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the default font size in CSS pixels.
    /// </summary>
    public double FontSize { get; set; } = 16;

    /// <summary>
    /// Gets or sets a callback to customize the IO options (e.g., the HTTP
    /// handler, storage or cache factories) after the defaults have been
    /// derived from the other properties.
    /// </summary>
    public Action<IoOptions>? ConfigureIo { get; set; }

    /// <summary>
    /// Gets or sets a callback to extend or replace the AngleSharp
    /// configuration before the browsing context is created.
    /// </summary>
    public Func<IConfiguration, IConfiguration>? Configure { get; set; }

    /// <summary>
    /// Creates the IO options of an engine. Another window of a profile shares its data, but
    /// gets an HTTP handler of its own: a handler cannot be configured anymore once it has sent
    /// a request, which the IO setup of every engine does.
    /// </summary>
    internal IoOptions CreateIoOptions(IoOptions? profile = null)
    {
        var options = profile is not null ? new IoOptions
        {
            CookieHandler = profile.CookieHandler,
            StorageFactory = profile.StorageFactory,
            IndexedDbFactory = profile.IndexedDbFactory,
            CacheFactory = profile.CacheFactory,
            HttpCache = profile.HttpCache,
        } : ProfileDirectory is null
            ? IoOptions.CreateTemporary()
            : IoOptions.CreatePersistent(ProfileDirectory);

        options.ClipboardPlatform = ClipboardPlatform;

        // pages register workers once they see navigator.serviceWorker (and fail on a null one);
        // the workers are installed and activated, but don't run, so requests go to the network
        options.ServiceWorkerPlatform = new ServiceWorkerPlatform();

        // like any browser: servers (e.g. CDNs keeping files compressed) may send gzip unasked
        options.HttpHandler.AutomaticDecompression = DecompressionMethods.All;

        if (UserAgent is not null)
        {
            options.Navigator.UserAgent = UserAgent;
        }

        ConfigureIo?.Invoke(options);
        return options;
    }
}
