namespace Vision.Pages;

using System;
using AngleSharp.Browser;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;

/// <summary>
/// The diagnostics tab: shows what the inspected engine collected about its current page
/// (errors, console output, requests) next to a summary of the document. The tabs are plain
/// HTML switched by .NET event handlers; the content is rendered from C# as well.
/// </summary>
public sealed class DiagnosticsPage : HtmlPage
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromSeconds(1);
    private static readonly string[] Tabs = ["overview", "errors", "console", "network"];
    private const int MaxShownEntries = 300;

    private readonly BrowserEngine _target;
    private readonly Timer _timer;
    private PageSummary? _summary;
    private int _isRefreshScheduled;
    private volatile bool _isDisposed;

    public DiagnosticsPage(string? colorScheme, BrowserEngine target)
        : base("diagnostics", colorScheme)
    {
        _target = target;
        _timer = new Timer(_ => _ = RefreshAsync());
        _target.Diagnostics.Changed += OnTargetChanged;
        _target.StateChanged += OnTargetChanged;
    }

    public override void Dispose()
    {
        _isDisposed = true;
        _target.Diagnostics.Changed -= OnTargetChanged;
        _target.StateChanged -= OnTargetChanged;
        _timer.Dispose();
        base.Dispose();
    }

    protected override void Wire(IDocument document)
    {
        foreach (var tab in Tabs)
        {
            OnClick(document, $"tab-{tab}", () => Select(document, tab));
        }

        OnClick(document, "clear", _target.Diagnostics.Clear);
        ScheduleRefresh();
    }

    private static void Select(IDocument document, string selected)
    {
        foreach (var tab in Tabs)
        {
            document.GetElementById($"tab-{tab}")?.ClassList.ToggleToken("active", tab == selected);
            document.GetElementById($"panel-{tab}")?.ClassList.ToggleToken("active", tab == selected);
        }
    }

    private void OnTargetChanged(object? sender, EventArgs e) => ScheduleRefresh();

    /// <summary>
    /// Pages report a lot while loading, so changes are collected for a moment before the
    /// diagnostics are rendered again.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (!_isDisposed && Interlocked.Exchange(ref _isRefreshScheduled, 1) == 0)
        {
            _timer.Change(RefreshDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshAsync()
    {
        Volatile.Write(ref _isRefreshScheduled, 0);

        try
        {
            // a page busy with a script must not hold up the rest of the diagnostics
            using (var timeout = new CancellationTokenSource(SummaryTimeout))
            {
                var viewport = $"{_target.ViewportWidth} × {_target.ViewportHeight}";
                _summary = await _target.InvokeAsync(document => PageSummary.Create(document, viewport), timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var requests = _target.Diagnostics.Requests.ToArray();
            var messages = _target.Diagnostics.Messages.ToArray();
            var summary = _summary;
            await Engine.InvokeAsync(document =>
            {
                if (document is not null)
                {
                    Render(document, summary, requests, messages);
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Rendering the diagnostics failed: {ex}");
        }
    }

    private static void Render(IDocument document, PageSummary? summary, NetworkEntry[] requests, DiagnosticMessage[] messages)
    {
        var errors = messages.Where(m => m.Kind == DiagnosticKind.Error).ToArray();
        var output = messages.Where(m => m.Kind == DiagnosticKind.Console).ToArray();
        var failed = requests.Count(IsFailed);

        SetCount(document, "count-errors", errors.Length, errors.Length > 0);
        SetCount(document, "count-console", output.Length, false);
        SetCount(document, "count-network", requests.Length, false);

        Replace(document, "cards", Card("Requests", requests.Length.ToString(), false) +
            Card("Failed", failed.ToString(), failed > 0) +
            Card("Errors", errors.Length.ToString(), errors.Length > 0) +
            Card("Transferred", FormatSize(requests.Sum(m => m.ContentLength ?? 0)), false) +
            Card("Elements", summary?.Elements.ToString() ?? "–", false) +
            Card("Load time", FormatLoadTime(requests), false));
        Replace(document, "facts", summary is null ? Empty("No page loaded.") : string.Concat(
            Fact("Address", summary.Url),
            Fact("Title", summary.Title.Length > 0 ? summary.Title : "(none)"),
            Fact("State", summary.ReadyState),
            Fact("Content type", $"{summary.ContentType} ({summary.CharacterSet})"),
            Fact("Resources", $"{summary.Scripts} scripts, {summary.StyleSheets} style sheets, {summary.Images} images"),
            Fact("Links and forms", $"{summary.Links} links, {summary.Forms} forms"),
            Fact("Cookies", summary.Cookies.ToString()),
            Fact("Viewport", summary.Viewport)));
        Replace(document, "errors", errors.Length == 0 ? Empty("No errors on this page.") : Messages(errors, "message error"));
        Replace(document, "console", output.Length == 0 ? Empty("Nothing was logged.") : Messages(output, "message"));
        Replace(document, "network", requests.Length == 0 ? Empty("No requests yet.") : string.Concat(requests.TakeLast(MaxShownEntries).Reverse().Select(Request)));
    }

    private static void SetCount(IDocument document, string id, int count, bool isAlert)
    {
        if (document.GetElementById(id) is { } badge)
        {
            badge.TextContent = count.ToString();
            badge.ClassList.ToggleToken("alert", isAlert);
        }
    }

    private static void Replace(IDocument document, string id, string html)
    {
        if (document.GetElementById(id) is { } element && element.InnerHtml != html)
        {
            element.InnerHtml = html;
        }
    }

    private static string Card(string label, string value, bool isAlert) =>
        $"""<div class="card{(isAlert ? " alert" : "")}"><div class="value">{Encode(value)}</div><div class="label">{Encode(label)}</div></div>""";

    private static string Fact(string label, string value) =>
        $"""<div class="fact"><div class="label">{Encode(label)}</div><div class="value">{Encode(value)}</div></div>""";

    private static string Empty(string text) => $"""<p class="empty">{Encode(text)}</p>""";

    private static string Messages(DiagnosticMessage[] messages, string className)
    {
        var html = new StringBuilder();

        // the latest first, as they matter most
        foreach (var message in messages.TakeLast(MaxShownEntries).Reverse())
        {
            html.Append($"""<div class="{className}"><span class="time">{message.Time:HH:mm:ss.fff}</span>{Encode(message.Text)}</div>""");
        }

        return html.ToString();
    }

    private static string Request(NetworkEntry entry)
    {
        var (status, kind) = entry switch
        {
            { IsPending: true } => ("…", "pending"),
            _ when IsFailed(entry) => (entry.StatusCode == 0 ? "ERR" : ((int)entry.StatusCode!).ToString(), "failed"),
            { StatusCode: >= (System.Net.HttpStatusCode)300 } => (((int)entry.StatusCode).ToString(), "redirect"),
            _ => (((int)entry.StatusCode!).ToString(), "ok"),
        };
        var meta = string.Join(" · ", new[]
        {
            entry.Method,
            entry.ContentType?.Split(';')[0],
            entry.ContentLength is long length ? FormatSize(length) : null,
            entry.Duration is TimeSpan duration ? $"{duration.TotalMilliseconds:0} ms" : null,
        }.Where(m => !string.IsNullOrEmpty(m)));
        return $"""
            <div class="entry">
              <div class="entry-head"><span class="status {kind}">{Encode(status)}</span><span class="name">{Encode(GetName(entry.Url))}</span></div>
              <div class="url">{Encode(Shorten(entry.Url))}</div>
              <div class="meta">{Encode(meta)}</div>
            </div>
            """;
    }

    private static bool IsFailed(NetworkEntry entry) =>
        entry.StatusCode is { } status && (status == 0 || (int)status >= 400);

    private static string GetName(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "data: URL";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            return string.IsNullOrEmpty(segment) ? uri.Host : Uri.UnescapeDataString(segment);
        }

        return url;
    }

    private static string Shorten(string url) => url.Length > 160 ? $"{url[..157]}…" : url;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} kB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private static string FormatLoadTime(NetworkEntry[] requests)
    {
        var done = requests.Where(m => m.Duration is not null).ToArray();

        if (done.Length == 0)
        {
            return "–";
        }

        var span = done.Max(m => m.StartedAt + m.Duration!.Value) - requests.Min(m => m.StartedAt);
        return span.TotalSeconds >= 1 ? $"{span.TotalSeconds:0.0} s" : $"{span.TotalMilliseconds:0} ms";
    }

    /// <summary>
    /// What the diagnostics show about the inspected document; read on its engine's event loop.
    /// </summary>
    private sealed record PageSummary(
        string Url,
        string Title,
        string ReadyState,
        string ContentType,
        string CharacterSet,
        int Elements,
        int Scripts,
        int StyleSheets,
        int Images,
        int Links,
        int Forms,
        int Cookies,
        string Viewport)
    {
        public static PageSummary? Create(IDocument? document, string viewport)
        {
            if (document is null)
            {
                return null;
            }

            var cookies = 0;

            try
            {
                cookies = document.Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch (Exception)
            {
                // documents without an origin (e.g., about:blank) have no cookies
            }

            return new PageSummary(
                document.Url,
                document.Title ?? string.Empty,
                document.ReadyState.ToString(),
                document.ContentType,
                document.CharacterSet,
                document.All.Length,
                document.Scripts.Length,
                document.StyleSheets.Length,
                document.Images.Length,
                document.Links.Length,
                document.Forms.Length,
                cookies,
                viewport);
        }
    }
}
