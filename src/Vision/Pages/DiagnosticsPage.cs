namespace Vision.Pages;

using System;
using AngleSharp.Browser;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

/// <summary>
/// The diagnostics tab: shows what the inspected engine collected about its current page
/// (errors, console output, requests, frame timings) next to a summary of the document. The tabs are plain
/// HTML switched by .NET event handlers; the content is rendered from C# as well.
/// </summary>
public sealed class DiagnosticsPage : HtmlPage
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromSeconds(1);
    private static readonly string[] Tabs = ["overview", "errors", "console", "network", "performance"];
    private const int MaxShownEntries = 300;
    private const int MaxShownStackLines = 40;
    private const int MaxTicks = 5;
    private const int SlowestFrames = 8;
    private const int FrameBarWidth = 5;
    private const int PlotHeight = 120;
    private const double FrameBudget = 1000.0 / 60;
    // the engine keeps this many frames; with the oldest gone the first one is unknown
    private const int MaxFramesKept = 600;

    private readonly BrowserEngine _target;
    private readonly Timer _timer;
    private readonly Dictionary<string, string> _shown = [];
    private readonly HashSet<long> _expandedRequests = [];
    private PageSummary? _summary;
    private string _active = Tabs[0];
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

        document.GetElementById("network")?.AddEventListener("click", (_, ev) =>
        {
            if ((ev.OriginalTarget as IElement)?.Closest("[data-request]") is { } row && long.TryParse(row.GetAttribute("data-request"), out var id))
            {
                if (!_expandedRequests.Remove(id))
                {
                    _expandedRequests.Add(id);
                }

                _ = RefreshAsync();
            }
        });
        OnClick(document, "clear", _target.Diagnostics.Clear);
        ScheduleRefresh();
    }

    private void Select(IDocument document, string selected)
    {
        _active = selected;
        _ = RefreshAsync();

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
            var frames = _target.Diagnostics.Frames.ToArray();
            var summary = _summary;
            await Engine.InvokeAsync(document =>
            {
                if (document is not null)
                {
                    Render(document, summary, requests, messages, frames);
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

    /// <summary>
    /// Only the tab being looked at is rendered, as this page has to keep up with the inspected
    /// one; the counts in the tabs are always up to date.
    /// </summary>
    private void Render(IDocument document, PageSummary? summary, NetworkEntry[] requests, DiagnosticMessage[] messages, FrameTiming[] frames)
    {
        var errors = messages.Where(m => m.Kind == DiagnosticKind.Error).ToArray();
        var output = messages.Where(m => m.Kind == DiagnosticKind.Console).ToArray();

        SetCount(document, "count-errors", errors.Length, errors.Length > 0);
        SetCount(document, "count-console", output.Length, false);
        SetCount(document, "count-network", requests.Length, false);
        SetCount(document, "count-performance", frames.Length, false);

        switch (_active)
        {
            case "overview":
                RenderOverview(document, summary, requests, errors, frames);
                break;
            case "errors":
                Replace(document, "errors", errors.Length == 0 ? Empty("No errors on this page.") : Errors(errors));
                break;
            case "console":
                Replace(document, "console", output.Length == 0 ? Empty("Nothing was logged.") : Messages(output, "message"));
                break;
            case "network":
                Replace(document, "network", requests.Length == 0 ? Empty("No requests yet.") : Waterfall(requests, frames));
                break;
            case "performance":
                Replace(document, "performance", frames.Length == 0 ? Empty("Nothing was painted yet.") : Performance(frames));
                break;
        }
    }

    private void RenderOverview(IDocument document, PageSummary? summary, NetworkEntry[] requests, DiagnosticMessage[] errors, FrameTiming[] frames)
    {
        var failed = requests.Count(IsFailed);

        Replace(document, "cards", Card("Requests", requests.Length.ToString(), false) +
            Card("Failed", failed.ToString(), failed > 0) +
            Card("Errors", errors.Length.ToString(), errors.Length > 0) +
            Card("Transferred", FormatSize(requests.Sum(m => m.ContentLength ?? 0)), false) +
            Card("Elements", summary?.Elements.ToString() ?? "–", false) +
            Card("Load time", FormatLoadTime(requests), false) +
            Card("First paint", FormatFirstPaint(requests, frames), false) +
            Card("Frames", frames.Length.ToString(), false));
        Replace(document, "facts", summary is null ? Empty("No page loaded.") : string.Concat(
            Fact("Address", summary.Url),
            Fact("Title", summary.Title.Length > 0 ? summary.Title : "(none)"),
            Fact("State", summary.ReadyState),
            Fact("Content type", $"{summary.ContentType} ({summary.CharacterSet})"),
            Fact("Rendering mode", summary.CompatMode),
            Fact("Language", summary.Language),
            Fact("Referrer", summary.Referrer),
            Fact("Resources", $"{summary.Scripts} scripts, {summary.StyleSheets} style sheets ({summary.StyleRules} rules), {summary.Images} images, {summary.Frames} iframes"),
            Fact("Transfers by type", TransfersByType(requests)),
            Fact("Links and forms", $"{summary.Links} links, {summary.Forms} forms"),
            Fact("Cookies", summary.Cookies.ToString()),
            Fact("Viewport", summary.Viewport)));
    }

    private static void SetCount(IDocument document, string id, int count, bool isAlert)
    {
        if (document.GetElementById(id) is { } badge)
        {
            badge.TextContent = count.ToString();
            badge.ClassList.ToggleToken("alert", isAlert);
        }
    }

    private void Replace(IDocument document, string id, string html)
    {
        // comparing with what was set, as the serialized DOM differs from the source
        if ((!_shown.TryGetValue(id, out var current) || current != html) && document.GetElementById(id) is { } element)
        {
            _shown[id] = html;
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

    /// <summary>
    /// The errors with all that is known about them, the latest first. The same error thrown
    /// again and again is shown once.
    /// </summary>
    private static string Errors(DiagnosticMessage[] errors)
    {
        var html = new StringBuilder();
        var groups = errors.TakeLast(MaxShownEntries).GroupBy(m => (m.Text, m.ErrorType, m.Source, m.Line, m.Column, m.StackTrace));

        foreach (var group in groups.OrderByDescending(m => m.Last().Time))
        {
            var error = group.Last();
            var type = error.ErrorType is null ? string.Empty : $"{error.ErrorType}: ";
            var repeat = group.Count() > 1 ? $"""<span class="badge repeat">× {group.Count()}</span>""" : string.Empty;
            html.Append($"""<div class="message error"><span class="time">{error.Time:HH:mm:ss.fff}</span><div class="head">{Encode(type + error.Text)}{repeat}</div>""");

            if (FormatLocation(error) is { } location)
            {
                html.Append($"""<div class="where">{Encode(location)}</div>""");
            }

            if (!string.IsNullOrWhiteSpace(error.StackTrace))
            {
                html.Append($"""<pre class="stack">{Encode(LimitLines(error.StackTrace))}</pre>""");
            }

            html.Append("</div>");
        }

        return html.ToString();
    }

    private static string? FormatLocation(DiagnosticMessage error)
    {
        if (error.Source is null && error.Line is null)
        {
            return null;
        }

        var position = error.Line is int line ? $"line {line}, column {error.Column ?? 0}" : string.Empty;
        return error.Source is null ? position : $"{error.Source} {position}".TrimEnd();
    }

    private static string LimitLines(string text)
    {
        var lines = text.Split('\n');
        return lines.Length <= MaxShownStackLines
            ? text
            : $"{string.Join('\n', lines.Take(MaxShownStackLines))}\n… and {lines.Length - MaxShownStackLines} more lines";
    }

    /// <summary>
    /// When each request started and ended, on one time axis in the order they were sent.
    /// </summary>
    private string Waterfall(NetworkEntry[] requests, FrameTiming[] frames)
    {
        var shown = requests.TakeLast(MaxShownEntries).ToArray();
        var now = DateTimeOffset.Now;
        var start = shown.Min(m => m.StartedAt);
        var end = shown.Max(m => m.StartedAt + (m.Duration ?? now - m.StartedAt));
        var total = Math.Max((end - start).TotalMilliseconds, 1);
        var html = new StringBuilder();
        var firstPaint = GetFirstPaint(frames);
        var paintAt = firstPaint is { } paint ? (paint - start).TotalMilliseconds : (double?)null;
        var paintNote = paintAt is >= 0 and var at ? $" · first paint at {FormatTime(at)} (orange line)" : string.Empty;
        html.Append($"""<div class="summary">{shown.Length} {(shown.Length == 1 ? "request" : "requests")} · {FormatSize(shown.Sum(m => m.ContentLength ?? 0))} · {FormatTime(total)}{paintNote}. Select a request for its details.</div>""");
        html.Append("""<div class="waterfall"><div class="axis">""");
        var ticks = GetTicks(total);

        foreach (var tick in ticks)
        {
            // the last label would stick out of the panel
            var position = tick / total * 100;
            html.Append(position > 85
                ? $"""<span style="right:{100 - position:0.##}%">{FormatTime(tick)}</span>"""
                : $"""<span style="left:{position:0.##}%">{FormatTime(tick)}</span>""");
        }

        html.Append("</div>");

        foreach (var tick in ticks)
        {
            html.Append($"""<div class="gridline" style="left:{tick / total * 100:0.##}%"></div>""");
        }

        if (paintAt is >= 0 and var line && line <= total)
        {
            html.Append($"""<div class="gridline paint" style="left:{line / total * 100:0.##}%"></div>""");
        }

        foreach (var entry in shown)
        {
            html.Append(Request(entry, start, total, now));
        }

        return html.Append("</div>").ToString();
    }

    /// <summary>
    /// A few round positions on an axis of the given length.
    /// </summary>
    private static double[] GetTicks(double total)
    {
        var rough = total / (MaxTicks - 1);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(m => m >= rough);
        return Enumerable.Range(0, (int)(total / step) + 1).Select(m => m * step).ToArray();
    }

    private string Request(NetworkEntry entry, DateTimeOffset start, double total, DateTimeOffset now)
    {
        var (status, kind) = entry switch
        {
            { IsPending: true } => ("…", "pending"),
            _ when IsFailed(entry) => (entry.StatusCode == 0 ? "ERR" : ((int)entry.StatusCode!).ToString(), "failed"),
            { StatusCode: >= (System.Net.HttpStatusCode)300 } => (((int)entry.StatusCode).ToString(), "redirect"),
            _ => (((int)entry.StatusCode!).ToString(), "ok"),
        };
        var duration = (entry.Duration ?? now - entry.StartedAt).TotalMilliseconds;
        var left = (entry.StartedAt - start).TotalMilliseconds / total * 100;
        var width = duration / total * 100;
        var bar = kind is "failed" or "pending" ? $"bar {kind}" : "bar";
        var id = entry.StartedAt.UtcTicks;
        var details = _expandedRequests.Contains(id) ? RequestDetails(entry, (entry.StartedAt - start).TotalMilliseconds, duration) : string.Empty;
        return $"""
            <div class="request" data-request="{id}">
              <div class="request-head"><span class="status {kind}">{Encode(status)}</span><span class="name">{Encode(GetName(entry.Url))}</span><span class="took">{FormatTime(duration)}</span></div>
              <div class="track"><div class="{bar}" style="left:{left:0.##}%;width:{width:0.##}%"></div></div>
              {details}
            </div>
            """;
    }

    private static string RequestDetails(NetworkEntry entry, double startedAfter, double duration)
    {
        var meta = string.Join(" · ", new[]
        {
            entry.Method,
            entry.ContentType?.Split(';')[0],
            entry.ContentLength is long length ? FormatSize(length) : null,
            $"from {FormatTime(startedAfter)} to {FormatTime(startedAfter + duration)}",
        }.Where(m => !string.IsNullOrEmpty(m)));
        return $"""
            <div class="request-details">
              <div class="line">{Encode(Shorten(entry.Url))}</div>
              <div class="line">{Encode(meta)}</div>
              {Headers("Request headers", entry.RequestHeaders)}
              {Headers("Response headers", entry.ResponseHeaders)}
            </div>
            """;
    }

    private static string Headers(string title, IReadOnlyDictionary<string, string> headers)
    {
        var lines = headers
            .Where(m => !string.IsNullOrEmpty(m.Value))
            .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"""<div class="line"><span class="key">{Encode(m.Key)}:</span> {Encode(m.Value)}</div>""");
        var html = string.Concat(lines);
        return html.Length == 0 ? string.Empty : $"""<div class="label">{Encode(title)}</div>{html}""";
    }

    /// <summary>
    /// How long the latest frames took on the way to the screen, with the slowest ones as a table.
    /// </summary>
    private string Performance(FrameTiming[] frames)
    {
        var html = new StringBuilder();
        var totals = frames.Select(GetTotal).ToArray();
        html.Append("""<div class="cards">""");
        html.Append(Card("Average layout", FormatTime(frames.Average(m => m.Layout.TotalMilliseconds)), false));
        html.Append(Card("Average raster", FormatTime(frames.Average(m => m.Raster.TotalMilliseconds)), false));
        html.Append(Card("Slowest frame", FormatTime(totals.Max()), totals.Max() > 100));
        html.Append(Card("Over budget", $"{totals.Count(m => m > FrameBudget)} of {frames.Length}", false));
        html.Append(Card("Median frame", FormatTime(totals.Order().ElementAt(totals.Length / 2)), false));
        html.Append(Card("Laid out / patched / moved", string.Join(" / ", Enum.GetValues<FrameKind>().Select(k => frames.Count(m => m.Kind == k))), false));
        html.Append("</div>");

        // the panel is the viewport, so it tells how many frames fit
        var shown = frames.TakeLast(Math.Max(10, (Engine.ViewportWidth - 24) / FrameBarWidth)).ToArray();
        var scale = Math.Max(FrameBudget * 2, shown.Max(GetTotal));
        html.Append("""<div class="chart-title"><span>Latest frames</span><span class="legend"><i class="swatch layout"></i>Layout<i class="swatch raster"></i>Raster</span></div>""");
        html.Append("""<div class="plot">""");
        html.Append($"""<span class="scale" style="top:0">{FormatTime(scale)}</span>""");
        html.Append($"""<div class="budget" style="bottom:{FrameBudget / scale * 100:0.##}%"></div>""");
        html.Append($"""<span class="scale" style="bottom:{FrameBudget / scale * 100:0.##}%">60 fps</span>""");

        foreach (var frame in shown)
        {
            var layout = Math.Max(frame.Layout.TotalMilliseconds / scale * PlotHeight, 1);
            var raster = Math.Max(frame.Raster.TotalMilliseconds / scale * PlotHeight, 1);
            var tip = $"{frame.Time:HH:mm:ss.fff} · {frame.Kind} · layout {FormatTime(frame.Layout.TotalMilliseconds)}, raster {FormatTime(frame.Raster.TotalMilliseconds)}";
            html.Append($"""<div class="frame"><div class="segment raster" style="height:{raster:0.#}px"></div><div class="segment layout" style="height:{layout:0.#}px"></div><span class="tip">{Encode(tip)}</span></div>""");
        }

        html.Append("</div>");
        html.Append("""<h2 class="section">Slowest frames</h2><div class="frame-row head"><span>Time</span><span>Kind</span><span>Layout</span><span>Raster</span></div>""");

        foreach (var frame in frames.OrderByDescending(GetTotal).Take(SlowestFrames))
        {
            html.Append($"""<div class="frame-row"><span>{frame.Time:HH:mm:ss.fff}</span><span>{frame.Kind}</span><span>{FormatTime(frame.Layout.TotalMilliseconds)}</span><span>{FormatTime(frame.Raster.TotalMilliseconds)}</span></div>""");
        }

        return html.ToString();
    }

    private static double GetTotal(FrameTiming frame) => (frame.Layout + frame.Raster).TotalMilliseconds;

    private static DateTimeOffset? GetFirstPaint(FrameTiming[] frames) =>
        frames.Length is 0 or >= MaxFramesKept ? null : frames[0].Time + frames[0].Layout + frames[0].Raster;

    private static string FormatFirstPaint(NetworkEntry[] requests, FrameTiming[] frames) =>
        requests.Length > 0 && GetFirstPaint(frames) is { } paint ? FormatTime((paint - requests.Min(m => m.StartedAt)).TotalMilliseconds) : "–";

    private static string TransfersByType(NetworkEntry[] requests)
    {
        var groups = requests.GroupBy(m => GetResourceType(m.ContentType))
            .OrderByDescending(m => m.Sum(r => r.ContentLength ?? 0))
            .Select(m => $"{m.Key}: {m.Count()} ({FormatSize(m.Sum(r => r.ContentLength ?? 0))})");
        return requests.Length == 0 ? "None" : string.Join(", ", groups);
    }

    private static string GetResourceType(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        null or "" => "Other",
        "text/html" or "application/xhtml+xml" => "Documents",
        "text/css" => "Style sheets",
        "application/json" => "Data",
        var type when type.Contains("javascript") || type.Contains("ecmascript") => "Scripts",
        var type when type.StartsWith("image/") => "Images",
        var type when type.StartsWith("font/") || type.Contains("font") => "Fonts",
        var type when type.StartsWith("audio/") || type.StartsWith("video/") => "Media",
        _ => "Other",
    };

    private static string FormatTime(double milliseconds) => milliseconds switch
    {
        >= 1000 => $"{milliseconds / 1000:0.##} s",
        >= 10 => $"{milliseconds:0} ms",
        _ => $"{milliseconds:0.#} ms",
    };

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
        int StyleRules,
        int Images,
        int Frames,
        string CompatMode,
        string Language,
        string Referrer,
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
                document.StyleSheets.OfType<ICssStyleSheet>().Sum(m => m.Rules.Length),
                document.Images.Length,
                document.QuerySelectorAll("iframe").Length,
                document.CompatMode,
                document.DocumentElement?.GetAttribute("lang") ?? "(not set)",
                document.Referrer is { Length: > 0 } referrer ? referrer : "(none)",
                document.Links.Length,
                document.Forms.Length,
                cookies,
                viewport);
        }
    }
}
