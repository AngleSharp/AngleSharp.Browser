namespace Vision.Pages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Io;
using Avalonia.Threading;
using Vision.ViewModels;

/// <summary>
/// The browser's own pages at addresses of the "vision:" scheme (e.g., "vision:settings"),
/// shown in a browser window like any page. The engine of the window asks for the source
/// (<see cref="HandleAsync"/>); once it shows the document, the page is wired up.
/// </summary>
public sealed class VisionPages : IDisposable
{
    public const string Scheme = "vision";

    /// <summary>
    /// The pages there are: name, title and what they are for.
    /// </summary>
    public static IReadOnlyList<VisionPageInfo> Available { get; } =
    [
        new("settings", "Settings", "Start page, privacy, appearance and network settings of new windows."),
        new("about", "About Vision", "The version of Vision, its components and contributors."),
        new("pages", "Vision pages", "The list of Vision's own pages."),
    ];

    private readonly IBrowserHost _host;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, HtmlPage> _pending = new(StringComparer.Ordinal);
    private BrowserEngine? _engine;
    private HtmlPage? _current;
    private IDocument? _wired;
    private bool _isDisposed;

    public VisionPages(IBrowserHost host)
    {
        _host = host;
    }

    public static string GetAddress(string name) => $"{Scheme}:{name}";

    /// <summary>
    /// Wires up the pages the given engine (of the window) shows from now on.
    /// </summary>
    public void Attach(BrowserEngine engine)
    {
        _engine = engine;
        engine.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Answers a request of the scheme with the source of the page, or with the list of pages
    /// if there is no page of that name.
    /// </summary>
    public async Task<IResponse?> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var url = request.Address.Href;
        var name = GetName(url);
        // the host (its theme and settings) belongs to the UI thread, while the engine asks on
        // its own thread when the page follows a link
        var page = Dispatcher.UIThread.CheckAccess()
            ? CreatePage(name)
            : await Dispatcher.UIThread.InvokeAsync(() => CreatePage(name), DispatcherPriority.Normal, cancellationToken);
        var response = new DefaultResponse
        {
            Address = request.Address,
            StatusCode = page is PageListPage { IsNotFound: true } ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            Content = new MemoryStream(Encoding.UTF8.GetBytes(page.GetSource())),
        };
        response.Headers[HeaderNames.ContentType] = "text/html; charset=utf-8";

        lock (_lock)
        {
            if (_isDisposed)
            {
                page.Dispose();
            }
            else
            {
                // only the latest request of an address is shown (others went to an iframe, or got replaced)
                if (_pending.Remove(url, out var replaced))
                {
                    replaced.Dispose();
                }

                _pending[url] = page;
            }
        }

        return response;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;

            foreach (var page in _pending.Values)
            {
                page.Dispose();
            }

            _pending.Clear();
            _current?.Dispose();
            _current = null;
        }

        if (_engine is not null)
        {
            _engine.StateChanged -= OnStateChanged;
        }
    }

    /// <summary>
    /// Creates the page of the given name, or the list of pages. Runs on the UI thread.
    /// </summary>
    private HtmlPage CreatePage(string name) => name switch
    {
        "settings" => _host.CreateSettingsPage(Close),
        "about" => new AboutPage(_host.ColorScheme, Close),
        _ => new PageListPage(_host.ColorScheme, name),
    };

    /// <summary>
    /// The name of the page at the address: "settings" for "vision:settings" (or "vision://settings/").
    /// </summary>
    public static string GetName(string url)
    {
        var rest = url.Length > Scheme.Length ? url[(Scheme.Length + 1)..] : string.Empty;
        var end = rest.IndexOfAny(['?', '#']);
        return (end >= 0 ? rest[..end] : rest).Trim('/').ToLowerInvariant();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // raised on the engine's thread, also while it holds the DOM: wiring has to wait for it
        if (_engine?.Document is { } document && !ReferenceEquals(document, _wired) && IsPage(document.Url))
        {
            _ = _engine.InvokeAsync(Wire);
        }
    }

    /// <summary>
    /// Runs on the event loop: wires up the page the engine shows, once it has been parsed.
    /// </summary>
    private void Wire(IDocument? document)
    {
        if (document is null || ReferenceEquals(document, _wired) || document.ReadyState == DocumentReadyState.Loading ||
            !ReferenceEquals(document, _engine?.Document))
        {
            return;
        }

        HtmlPage? page;
        HtmlPage? previous;

        lock (_lock)
        {
            if (_isDisposed || !_pending.Remove(document.Url, out page))
            {
                return;
            }

            previous = _current;
            _current = page;
            _wired = document;
        }

        previous?.Dispose();
        page.Attach(_engine!, document);
    }

    private static bool IsPage(string? url) => url?.StartsWith($"{Scheme}:", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// What the "Close" button of a page does in a window: back to where the user came from.
    /// </summary>
    private void Close()
    {
        if (_engine is { } engine)
        {
            _ = engine.CanGoBack ? engine.GoBackAsync() : engine.NavigateAsync(_host.StartPage);
        }
    }
}

/// <summary>
/// One of the browser's own pages.
/// </summary>
public sealed record VisionPageInfo(string Name, string Title, string Description)
{
    public string Address => VisionPages.GetAddress(Name);
}

/// <summary>
/// The list of the browser's own pages, also shown for an address of the scheme without a page.
/// </summary>
public sealed class PageListPage(string? colorScheme, string name) : HtmlPage("pages", colorScheme)
{
    /// <summary>
    /// Gets if a page that does not exist was asked for.
    /// </summary>
    public bool IsNotFound => name != "pages";

    protected override string Prepare(string html)
    {
        var items = new StringBuilder();

        foreach (var page in VisionPages.Available)
        {
            items.Append($"""
                <a class="item" href="{Encode(page.Address)}"><span class="name">{Encode(page.Title)}</span><span class="address">{Encode(page.Address)}</span><span class="description">{Encode(page.Description)}</span></a>
                """);
        }

        var notice = IsNotFound
            ? $"""<div class="notice" id="notice">There is no page at <strong>{Encode(VisionPages.GetAddress(name))}</strong>. These are the pages Vision has:</div>"""
            : string.Empty;

        return html
            .Replace("{{notice}}", notice)
            .Replace("{{pages}}", items.ToString());
    }

    protected override void Wire(IDocument document)
    {
    }
}
