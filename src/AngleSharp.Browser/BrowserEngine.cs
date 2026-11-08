namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Browser.Dom;
using AngleSharp.Browser.Dom.Events;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Html.Parser;
using AngleSharp.Io;
using AngleSharp.Io.Cookie;
using AngleSharp.Js;
using AngleSharp.Renderer;
using AngleSharp.Scripting;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// A UI-agnostic browser: it loads documents, paints the active document
/// into images and translates user input into DOM interactions. Hosts
/// (e.g., an Avalonia control) display the painted frames and forward input.
/// </summary>
/// <remarks>
/// All operations touching the DOM are serialized. Events may be raised from
/// any thread, so hosts need to marshal them to their UI thread.
/// </remarks>
public sealed partial class BrowserEngine : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(200);

    // An interval needs a delay that cannot complete synchronously (at least 1ms; browsers use
    // 4ms once nested), a negative delay means 0 (HTML 8.6) instead of waiting forever.
    private const string TimerClampScript = """
        (function (w) {
            function clamp(name, min) {
                var timer = w[name];
                // AngleSharp.Js 1.1.2 ignores assigning a window member, but not defining one
                Object.defineProperty(w, name, {
                    value: function (handler, timeout) { return timer.call(w, handler, Math.max(min, Number(timeout) || 0)); },
                    writable: true,
                    configurable: true,
                });
            }

            clamp('setTimeout', 0);
            clamp('setInterval', 1);
        })(window);
        """;
    private const string ColorSchemeFeature = "prefers-color-scheme";
    private static readonly TimeSpan SettleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InteractionDuration = TimeSpan.FromMilliseconds(600);
    private static readonly string[] TextContentTypes = [MimeTypeNames.Plain, MimeTypeNames.ApplicationJson, MimeTypeNames.DefaultJavaScript, MimeTypeNames.Css];

    private readonly BrowserOptions _options;
    private readonly IoOptions _ioOptions;
    private readonly ICookieProvider _cookies;
    private readonly IBrowsingContext _context;
    private readonly DefaultRenderDevice _device;
    private readonly IDomHarness _harness;
    private readonly IEventLoop? _loop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NavigationHistory _history = new();
    private readonly BrowserDiagnostics _diagnostics = new();
    private readonly Timer _heartbeat;
    private MutationObserver? _observer;
    private IDocument? _document;
    private IEventLoop? _eventLoop;
    private string _title = string.Empty;
    private PendingNavigation? _pendingNavigation;
    private int _navigationId;
    private long _animateUntil;
    private long _lastPaint;
    private int _loadCount;
    private int _layoutVersion;
    private int _scrollExtentVersion = -1;
    private Exception? _lastError;
    private double _maxScrollTop;
    private bool _wasReadyForLayout;
    private volatile bool _isPainting;
    private volatile bool _isDisposed;

    /// <summary>
    /// Creates a new browser engine.
    /// </summary>
    /// <param name="options">The options to use, if any.</param>
    public BrowserEngine(BrowserOptions? options = null)
        : this(options ?? new BrowserOptions(), null)
    {
    }

    /// <summary>
    /// Creates an engine, which shares the profile (cookies, storage, caches) of the given
    /// opener, if any.
    /// </summary>
    private BrowserEngine(BrowserOptions options, BrowserEngine? opener)
    {
        _options = options;

        _device = new DefaultRenderDevice
        {
            Category = DeviceCategory.Screen,
            ColorBits = 32,
            DeviceWidth = options.ScreenWidth,
            DeviceHeight = options.ScreenHeight,
            ViewPortWidth = Math.Max(1, options.ViewportWidth),
            ViewPortHeight = Math.Max(1, options.ViewportHeight),
            FontSize = options.FontSize,
            IsScripting = options.IsScriptingEnabled,
            Preferences = CreatePreferences(options.ColorScheme),
        };

        var ioOptions = options.CreateIoOptions(opener?._ioOptions);
        // every engine of a profile has to see the same cookies, not its own copy of the file
        _ioOptions = ioOptions;

        // the site information shows the certificate of the connection (unless the host verifies certificates itself)
        if (ioOptions.HttpHandler is { ServerCertificateCustomValidationCallback: null } handler)
        {
            TrackCertificates(handler, _certificates);
        }

        _cookies = opener?._cookies ?? new SynchronizedCookieProvider(new AdvancedCookieProvider(ioOptions.CookieHandler));
        var config = Configuration.Default
            // one loop for the page and its iframes: scripts of all of them run on it, and the
            // layout of the page holds it, as the DOM of an iframe is part of what it lays out
            .WithEventLoop(context => _eventLoop is null ? _eventLoop = new JsEventLoop(context) : new FrameEventLoop(_eventLoop))
            .WithRenderDevice(_device)
            .WithXml()
            .WithCss()
            .WithIo(ioOptions)
            .WithCookies(_cookies)
            .WithWasm()
            .WithRendering()
            .WithDefaultLoader(new LoaderOptions
            {
                IsResourceLoadingEnabled = true,
            });

        if (options.IsScriptingEnabled)
        {
            config = config
                .WithConsoleLogger(_ => new BrowserDiagnostics.ConsoleLogger(_diagnostics))
                .WithJs();
        }

        // the top-level context shares the engine's history (window.history), frames get their own
        var hasTopLevelHistory = false;
        config = config.With<IHistory>(_ =>
        {
            if (hasTopLevelHistory)
            {
                return new NavigationHistory();
            }

            hasTopLevelHistory = true;
            return _history;
        });
        // like in a browser, a script moving through the history continues before anything happens
        _history.TraversalRequested += delta => _ = Task.Run(() => TraverseAsync(delta, CancellationToken.None));
        _history.Changed += OnStateChanged;

        if (options.Configure is not null)
        {
            config = options.Configure(config);
        }

        _context = BrowsingContext.New(config);

        // AngleSharp.Io only reports the user agent via the navigator, but some servers refuse requests without one
        foreach (var requester in _context.GetServices<IRequester>().OfType<BaseRequester>())
        {
            requester.Requesting += (_, ev) =>
            {
                var request = ((RequestEvent)ev).Request;
                AddHeaders(request, ioOptions.Navigator.UserAgent, options.DefaultHeaders);
                _diagnostics.OnRequesting(request);
            };
            requester.Requested += (_, ev) => _diagnostics.OnRequested(((RequestEvent)ev).Request, ((RequestEvent)ev).Response);
        }

        if (options.IsScriptingEnabled)
        {
            // the default style sheet lacks the rule from the HTML standard (15.3.1)
            _context.GetService<ICssDefaultStyleSheetProvider>()?.AppendDefault("noscript { display: none !important }");

            // AngleSharp's setInterval with a delay of 0 schedules every round synchronously, so it
            // never yields and floods the event loop until memory runs out (dev.to does this); the
            // page's scripts get timers with the delays clamped before any of them runs (fixed in
            // AngleSharp 1.9.0, remove this once referenced)
            if (_context.GetService<IHtmlParser>() is HtmlParser parser && _context.GetService<JsScriptingService>() is { } scripting)
            {
                parser.Parsing += (_, ev) =>
                {
                    if (ev is AngleSharp.Html.Dom.Events.HtmlParseEvent { Document: { } document })
                    {
                        scripting.EvaluateScript(document, TimerClampScript, "text/javascript", "about:blank");
                        // AngleSharp.Js reports an event listener throwing on the window alone, with an
                        // ErrorEvent that only has the type "error" from AngleSharp 1.10 or
                        // AngleSharp.Js 1.2 (the listener of no type goes away once they are referenced)
                        DomEventHandler onError = (_, error) =>
                        {
                            if (error is ErrorEvent { Error: { } exception })
                            {
                                OnError(exception);
                            }
                        };
                        document.DefaultView?.AddEventListener("error", onError);
                        document.DefaultView?.AddEventListener(null!, onError);
                    }
                };
            }
        }
        // script errors (e.g., an API missing in the engine) would go unnoticed otherwise
        _context.AddEventListener("error", (_, ev) =>
        {
            if (ev is TrackEvent { Error: Exception error })
            {
                OnError(error);
            }
        });
        _harness = _context.GetDomHarness();
        _loop = _context.GetService<IEventLoop>();
        _harness.PaintInvalidated += OnPaintInvalidated;
        _heartbeat = new Timer(OnHeartbeat, null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>
    /// Raised when the visual output changed and a new frame should be painted.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>
    /// Raised when the page asks for a new window (e.g., a link with target="_blank" or a
    /// link clicked with Control). The host may create one with <see cref="CreateWindow()"/>
    /// and mark the request as handled; otherwise the current page navigates. Raised on the
    /// engine's thread.
    /// </summary>
    public event EventHandler<WindowRequestedEventArgs>? WindowRequested;

    /// <summary>
    /// Raised when the address, title, loading or history state changed.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised when an operation failed, e.g., a navigation or a script.
    /// </summary>
    public event EventHandler<Exception>? Error;

    /// <summary>
    /// Gets what happened while showing the current page (requests, errors, console output).
    /// </summary>
    public BrowserDiagnostics Diagnostics => _diagnostics;

    /// <summary>
    /// Gets or sets the color scheme reported to prefers-color-scheme, e.g., "light" or
    /// "dark"; see <see cref="BrowserOptions.ColorScheme"/>.
    /// </summary>
    public string? ColorScheme
    {
        get => _device.Preferences.TryGetValue(ColorSchemeFeature, out var value) ? value : null;
        set
        {
            if (value != ColorScheme)
            {
                _device.Preferences = CreatePreferences(value);
                OnLayoutChanged();
            }
        }
    }

    /// <summary>
    /// Gets the underlying browsing context.
    /// </summary>
    public IBrowsingContext Context => _context;

    /// <summary>
    /// Gets the currently displayed document, if any.
    /// </summary>
    public IDocument? Document => _document;

    /// <summary>
    /// Gets the address of the currently displayed document, or the address being navigated
    /// to while a navigation is in progress.
    /// </summary>
    public string? Url => Volatile.Read(ref _pendingNavigation)?.Url ?? _history.GetUrl(_document) ?? _document?.Url;

    /// <summary>
    /// Gets the title of the currently displayed document.
    /// </summary>
    public string Title => _title;

    /// <summary>
    /// Gets if a navigation is in progress.
    /// </summary>
    public bool IsLoading => Volatile.Read(ref _loadCount) > 0;

    /// <summary>
    /// Gets if there is a previous entry in the history.
    /// </summary>
    public bool CanGoBack
    {
        get => _history.CanGoBack;
    }

    /// <summary>
    /// Gets if there is a next entry in the history.
    /// </summary>
    public bool CanGoForward
    {
        get => _history.CanGoForward;
    }

    /// <summary>
    /// Gets the width of the viewport in CSS pixels.
    /// </summary>
    public int ViewportWidth => _device.ViewPortWidth;

    /// <summary>
    /// Gets the height of the viewport in CSS pixels.
    /// </summary>
    public int ViewportHeight => _device.ViewPortHeight;

    /// <summary>
    /// Gets the vertical scroll offset of the page.
    /// </summary>
    public double ScrollTop => _document?.DocumentElement is IElement root ? _harness.GetScrollTop(root, double.MaxValue) : 0;

    /// <summary>
    /// Turns user input from an address bar into an absolute address.
    /// </summary>
    /// <param name="address">The address as entered by the user.</param>
    /// <returns>The address to navigate to.</returns>
    public static string NormalizeAddress(string? address)
    {
        var trimmed = address?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "about:blank";
        }

        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
        {
            return new Uri(trimmed).AbsoluteUri;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"https://{trimmed}";
    }

    /// <summary>
    /// Navigates to the given address.
    /// </summary>
    /// <param name="address">The address to load; see <see cref="NormalizeAddress"/>.</param>
    /// <param name="cancellationToken">The token to cancel loading.</param>
    public Task NavigateAsync(string address, CancellationToken cancellationToken = default) =>
        LoadAsync(NormalizeAddress(address), false, cancellationToken);

    /// <summary>
    /// Loads the given HTML source as a document.
    /// </summary>
    /// <param name="html">The source code of the document.</param>
    /// <param name="address">The address to associate with the document.</param>
    /// <param name="cancellationToken">The token to cancel loading.</param>
    public Task LoadHtmlAsync(string html, string address = "about:blank", CancellationToken cancellationToken = default) =>
        TrackNavigationAsync(() => _context.OpenAsync(res => res.Content(html).Address(address), cancellationToken), address);

    /// <summary>
    /// Loads the current address again.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel loading.</param>
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var url = _history.Current;
        return url is null ? Task.CompletedTask : LoadAsync(url, true, cancellationToken);
    }

    /// <summary>
    /// Navigates to the previous entry in the history, if any.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel loading.</param>
    public Task GoBackAsync(CancellationToken cancellationToken = default) =>
        TraverseAsync(-1, cancellationToken);

    /// <summary>
    /// Navigates to the next entry in the history, if any.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel loading.</param>
    public Task GoForwardAsync(CancellationToken cancellationToken = default) =>
        TraverseAsync(1, cancellationToken);

    /// <summary>
    /// Changes the size of the viewport.
    /// </summary>
    /// <param name="width">The new width in CSS pixels.</param>
    /// <param name="height">The new height in CSS pixels.</param>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_device.ViewPortWidth != width || _device.ViewPortHeight != height)
        {
            _device.ViewPortWidth = width;
            _device.ViewPortHeight = height;
            OnLayoutChanged();
        }
    }

    /// <summary>
    /// Paints the current document using the current viewport.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    /// <returns>The painted PNG image or null if no document is available.</returns>
    public Task<RenderedImage?> PaintAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(Paint, cancellationToken);

    /// <summary>
    /// Runs a script in the current document, like a browser's developer console.
    /// </summary>
    /// <param name="script">The JavaScript to run.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    /// <returns>The script's completion value converted to .NET, or null without a document or scripting.</returns>
    public Task<object?> EvaluateScriptAsync(string script, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            if (_document is null || !_device.IsScripting)
            {
                return null;
            }

            var result = _document.ExecuteScript(script);
            Invalidate();
            return result;
        }, cancellationToken);

    /// <summary>
    /// Runs code against the current document, e.g., to read it or to wire up a page with .NET
    /// event handlers. AngleSharp is not thread-safe, so code touching the DOM outside of event
    /// handlers has to run like this.
    /// </summary>
    /// <param name="action">The code to run; it gets the current document, if any.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    /// <returns>The value returned by the code.</returns>
    public Task<T> InvokeAsync<T>(Func<IDocument?, T> action, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            SyncActiveDocument();
            return action(_document);
        }, cancellationToken);

    /// <summary>
    /// Runs code against the current document; see <see cref="InvokeAsync{T}"/>.
    /// </summary>
    /// <param name="action">The code to run; it gets the current document, if any.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task InvokeAsync(Action<IDocument?> action, CancellationToken cancellationToken = default) =>
        InvokeAsync(document =>
        {
            action(document);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Creates the engine of another window. It uses the same options and shares the profile
    /// (cookies, storage, caches), but has its own browsing context and history.
    /// </summary>
    /// <returns>The new engine, which the caller disposes.</returns>
    public BrowserEngine CreateWindow() => CreateWindow(_options);

    /// <summary>
    /// Creates the engine of another window with other options (e.g., changed settings). It
    /// shares the profile (cookies, storage, caches), so the profile directory of the given
    /// options is not used.
    /// </summary>
    /// <param name="options">The options of the new engine.</param>
    /// <returns>The new engine, which the caller disposes.</returns>
    public BrowserEngine CreateWindow(BrowserOptions options)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return new BrowserEngine(options, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _heartbeat.Dispose();

            lock (_scrollLock)
            {
                _scrollTimer?.Dispose();
                _scrollTimer = null;
            }

            _harness.PaintInvalidated -= OnPaintInvalidated;
            _observer?.Disconnect();
            _context.Dispose();
        }
    }

    private RenderedImage? Paint()
    {
        SyncActiveDocument();

        if (!IsReadyForLayout())
        {
            return null;
        }

        _isPainting = true;

        try
        {
            AdvanceClock();
            return _harness.PaintToPng();
        }
        catch (Exception ex)
        {
            OnError(ex);
            throw;
        }
        finally
        {
            _isPainting = false;
        }
    }

    /// <summary>
    /// AngleSharp parses documents, runs deferred scripts and applies style
    /// sheets outside of the event loop. Laying out concurrently can crash the
    /// renderer, so layout waits until the document is complete and all style
    /// sheets (also those added later on) have arrived.
    /// </summary>
    private bool IsReadyForLayout()
    {
        var document = _document;
        return document is not null &&
            (document.ReadyState == DocumentReadyState.Complete || IsTextDocument(document)) &&
            !document.QuerySelectorAll("link[rel~=stylesheet]")
                .OfType<ILoadableElement>()
                .Any(m => m.CurrentDownload is { Task.IsCompleted: false });
    }

    /// <summary>
    /// AngleSharp shows text responses (e.g., text/plain or JSON) in a
    /// generated document, which never leaves the loading state.
    /// </summary>
    private static bool IsTextDocument(IDocument document) =>
        TextContentTypes.Contains(document.ContentType, StringComparer.OrdinalIgnoreCase);

    private static void AddHeaders(Request request, string userAgent, IDictionary<string, string> defaults)
    {
        if (!request.Headers.ContainsKey(HeaderNames.UserAgent))
        {
            request.Headers[HeaderNames.UserAgent] = userAgent;
        }

        foreach (var header in defaults)
        {
            request.Headers.TryAdd(header.Key, header.Value);
        }
    }

    private static Dictionary<string, string> CreatePreferences(string? colorScheme)
    {
        var preferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(colorScheme))
        {
            preferences[ColorSchemeFeature] = colorScheme;
        }

        return preferences;
    }

    private async Task LoadAsync(string url, bool isTraversal, CancellationToken cancellationToken)
    {
        _history.IsTraversing = isTraversal;

        try
        {
            await TrackNavigationAsync(() => _context.OpenAsync(url, cancellationToken), url).ConfigureAwait(false);
        }
        finally
        {
            _history.IsTraversing = false;
        }
    }

    /// <summary>
    /// Moves through the history: within a document by firing popstate, else by loading the
    /// entry's address.
    /// </summary>
    private async Task TraverseAsync(int delta, CancellationToken cancellationToken)
    {
        var entry = _history.Move(delta, out var isSameDocument);

        if (entry is null)
        {
            return;
        }

        if (isSameDocument)
        {
            await RunExclusiveAsync(() =>
            {
                NavigationHistory.SetDocumentUrl(entry);
                _document?.DefaultView?.Dispatch(new PopStateEvent(entry.State));
                OnStateChanged();
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await LoadAsync(entry.Url, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrackNavigationAsync(Func<Task<IDocument>> navigate, string url)
    {
        // the address shows where we are going right away, like in any browser
        var navigation = new PendingNavigation(Interlocked.Increment(ref _navigationId), url);
        _diagnostics.OnNavigating();
        Volatile.Write(ref _pendingNavigation, navigation);
        Interlocked.Increment(ref _loadCount);
        OnStateChanged();

        try
        {
            try
            {
                await navigate().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnError(ex);
                await _context.OpenAsync(res => res.Content(CreateErrorPage(url, ex)).Address(url)).ConfigureAwait(false);
            }

            await RunExclusiveAsync(SyncActiveDocument).ConfigureAwait(false);
        }
        finally
        {
            // unless a later navigation took over the address already
            Interlocked.CompareExchange(ref _pendingNavigation, null, navigation);

            Interlocked.Decrement(ref _loadCount);
            OnStateChanged();
        }
    }

    /// <summary>
    /// Picks up a changed active document, which may come from our own
    /// navigations or from the page (links, forms, scripts).
    /// Requires exclusive access.
    /// </summary>
    private void SyncActiveDocument()
    {
        var active = _context.Active;

        if (!ReferenceEquals(active, _document))
        {
            _observer?.Disconnect();
            _observer = null;
            _document = active;
            _hovered = null;
            _pressed = null;
            _focusedFrameDocument = null;
            _hoveredFrames.Clear();
            _pointerFrame = null;
            _wasReadyForLayout = false;
            ResetScrollPosition();

            if (active is not null)
            {
                _history.Push(active.Url, active);
                _diagnostics.OnDocumentChanged(active.Url);
            }

            _title = string.Empty;
            KeepAnimating(SettleDuration);
            OnLayoutChanged();
            OnStateChanged();
        }

        if (active is not null && active.ReadyState != DocumentReadyState.Loading && (active.Title ?? string.Empty) != _title)
        {
            _title = active.Title ?? string.Empty;
            OnStateChanged();
        }

        SyncIcon(active);

        // a document still being parsed may not have a root to observe yet
        if (_observer is null && active?.DocumentElement is IElement root)
        {
            _observer = new MutationObserver((_, _) => OnDocumentMutated());
            _observer.Connect(root, childList: true, subtree: true, attributes: true, characterData: true);
            OnLayoutChanged();
        }
    }

    private async Task<T> RunExclusiveAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await RunEnteredAsync(action, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the action on the event loop, once the gate has been entered; releases it again.
    /// </summary>
    private async Task<T> RunEnteredAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        try
        {
            // AngleSharp is not thread-safe; the event loop serializes the work with scripts. A task
            // of the event loop that throws never completes, so the exception is carried out of it.
            Exception? error = null;
            var task = _loop is null ? Task.Run(action, cancellationToken) : _loop.EnqueueAsync(_ =>
            {
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    error = ex;
                    return default!;
                }
            });
            var result = await task.ConfigureAwait(false);

            if (error is not null)
            {
                ExceptionDispatchInfo.Throw(error);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunExclusiveAsync(Action action, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);

    private void OnHeartbeat(object? state)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_gate.CurrentCount > 0)
        {
            RunExclusiveAsync(() =>
            {
                SyncActiveDocument();

                if (!_wasReadyForLayout && IsReadyForLayout())
                {
                    _wasReadyForLayout = true;
                    KeepAnimating(SettleDuration);
                }
            }).ContinueWith(task => OnError(task.Exception!.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);
        }

        if (Environment.TickCount64 < Volatile.Read(ref _animateUntil))
        {
            // the last frame is checked for changes, it is laid out anew only if it animates
            Invalidate(isMutation: !_isLastFrameAnimating);
        }
    }

    // the harness knows if the last frame can be brought up to date (see TryReuseLastFrame), or
    // drops its layout if not (e.g. for a resource that arrived), which lays out the page
    private void OnPaintInvalidated(object? sender, EventArgs e)
    {
        if (!_isPainting)
        {
            Invalidate(isMutation: true);
        }
    }

    private void OnDocumentMutated()
    {
        if (!_isPainting)
        {
            OnLayoutChanged(isMutation: true);
        }
    }

    private void OnLayoutChanged(bool isMutation = false)
    {
        Interlocked.Increment(ref _layoutVersion);
        Invalidate(isMutation);
    }

    /// <summary>
    /// Keeps requesting frames for the given duration, such that resources
    /// arriving later and CSS transitions are picked up.
    /// </summary>
    private void KeepAnimating(TimeSpan duration)
    {
        var until = Environment.TickCount64 + (long)duration.TotalMilliseconds;

        if (until > Volatile.Read(ref _animateUntil))
        {
            Volatile.Write(ref _animateUntil, until);
        }
    }

    private sealed record PendingNavigation(int Id, string Url);

    // anything but a mutation of the document (resources, time, hover) has to be laid out, a
    // mutation may have left the last frame as it is (see PrepareFrame)
    private void Invalidate(bool isMutation = false)
    {
        if (!_isSyncingScroll)
        {
            _needsLayout = true;
            _needsFullLayout |= !isMutation;
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnError(Exception ex)
    {
        // AngleSharp.Js 1.2 tells the window as well as the context of the same error
        if (ReferenceEquals(Interlocked.Exchange(ref _lastError, ex), ex))
        {
            return;
        }

        _diagnostics.OnError(ex);
        Error?.Invoke(this, ex);
    }

    private static string CreateErrorPage(string url, Exception ex) => $"""
        <!doctype html>
        <html>
        <head><title>Problem loading page</title></head>
        <body style="font-family: sans-serif; color: #333">
        <h1>This page could not be loaded</h1>
        <p>An error occurred while loading <b>{WebUtility.HtmlEncode(url)}</b>:</p>
        <pre>{WebUtility.HtmlEncode(ex.Message)}</pre>
        </body>
        </html>
        """;
}
