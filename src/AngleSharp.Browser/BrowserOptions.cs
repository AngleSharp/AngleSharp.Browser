namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    /// Gets or sets the user agent to report. If null, the
    /// <see cref="DefaultUserAgent"/> is used.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets the user agent reported by default, e.g.
    /// "Mozilla/5.0 (X11; Linux x86_64) AngleSharp.Browser/0.3". It names the
    /// engine instead of claiming to be another browser: sites (e.g. bot
    /// protections) check a claim of being Chrome against the way Chrome
    /// behaves and refuse a browser that claims it without behaving alike.
    /// </summary>
    public static string DefaultUserAgent { get; } = CreateDefaultUserAgent();

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
    /// Gets or sets if the text of pages can be selected with the pointer (dragging, a double
    /// press for a word, a triple one for a paragraph, Shift to extend) and with Ctrl+A. Off by
    /// default, as the layouts then also record where their text comes from.
    /// </summary>
    public bool IsTextSelectionEnabled { get; set; }

    /// <summary>
    /// Gets or sets where the audio of the pages plays (e.g. an adapter of the platform's audio
    /// device). If null, it plays silently: media advances in real time, with its events, but
    /// isn't heard (<see cref="SilentAudioOutput"/>).
    /// </summary>
    public IAudioOutput? AudioOutput { get; set; }

    /// <summary>
    /// Gets the audio formats that play, in the order they are tried; by default those of
    /// <see cref="AudioCodecs"/> (WAV, MP3, Ogg Vorbis and Ogg Opus).
    /// </summary>
    public IList<IAudioCodec> AudioCodecs { get; } = Browser.AudioCodecs.CreateDefault();

    /// <summary>
    /// Gets or sets if pages may play audible media before the user interacted with them (a
    /// press or a key). Off by default, like in browsers; muted media always plays.
    /// </summary>
    public bool IsAutoplayAllowed { get; set; }

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
    /// Gets the handlers of custom URL schemes by their name (without the colon), e.g., "app"
    /// answering the requests of "app:settings". A handler answers null if there is nothing at
    /// the address. Addresses of these schemes are navigated to as entered.
    /// </summary>
    public IDictionary<string, Func<Request, CancellationToken, Task<IResponse?>>> SchemeHandlers { get; } =
        new Dictionary<string, Func<Request, CancellationToken, Task<IResponse?>>>(StringComparer.OrdinalIgnoreCase);

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

        options.Navigator.UserAgent = UserAgent ?? DefaultUserAgent;

        ConfigureIo?.Invoke(options);
        return options;
    }

    private static string CreateDefaultUserAgent()
    {
        var version = typeof(BrowserOptions).Assembly.GetName().Version ?? new Version(0, 0);
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "i686",
            _ => "x86_64",
        };

        // the platform tokens of the other browsers on the same system
        var platform = OperatingSystem.IsWindows()
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Windows NT 10.0; ARM64" : "Windows NT 10.0; Win64; x64")
            : OperatingSystem.IsMacOS()
            ? "Macintosh; Intel Mac OS X 10_15_7"
            : OperatingSystem.IsAndroid()
            ? "Linux; Android 10; K"
            : $"X11; Linux {architecture}";
        return $"Mozilla/5.0 ({platform}) AngleSharp.Browser/{version.Major}.{version.Minor}";
    }
}
