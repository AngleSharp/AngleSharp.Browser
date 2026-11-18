namespace Vision.Pages;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Io;
using Vision.Models;

/// <summary>
/// A playground for the requesters of the browser (vision:requester): sends a request with one
/// of them, like the engine of the window does for its pages (with its user agent, default
/// headers and, if wanted, its cookies), and shows the response. Requests can be saved under a
/// name (the button below the response asks for it in a dialog) and restored later from the
/// sheet the menu button in the top-left corner opens.
/// </summary>
public sealed class RequesterPage(string? colorScheme, SavedRequests saved) : HtmlPage("requester", colorScheme)
{
    /// <summary>
    /// The most of a response's body read, beyond it the rest is left out.
    /// </summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    // laying out a lot of text takes a while, the rest is cut off
    private const int MaxShownCharacters = 200_000;

    private static readonly HttpMethod[] Methods =
    [
        HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Head, HttpMethod.Options, HttpMethod.Trace,
    ];

    private static readonly string[] Tabs = ["body", "headers", "request"];

    private IRequester[] _requesters = [];
    private CancellationTokenSource? _sending;
    private IDocument? _document;
    private string? _active;
    private bool _isDisposed;

    public override void Dispose()
    {
        _isDisposed = true;
        _sending?.Cancel();
        saved.Changed -= OnSavedChanged;
        base.Dispose();
    }

    protected override string Prepare(string html) => html.Replace(
        "{{methods}}",
        string.Concat(Methods.Select(m => $"""<option value="{m}">{m.ToString().ToUpperInvariant()}</option>""")));

    protected override void Wire(IDocument document)
    {
        _document = document;
        // the requesters are asked in order, the first one supporting the scheme is used
        _requesters = [.. Engine.Context.GetServices<IRequester>()];
        var options = new StringBuilder("""<option value="auto">Automatic</option>""");

        for (var i = 0; i < _requesters.Length; i++)
        {
            options.Append($"""<option value="{i}">{Encode(GetName(_requesters[i]))}</option>""");
        }

        document.GetElementById("requester")!.InnerHtml = options.ToString();
        ShowSaved(document);
        // other windows of the profile may save requests as well
        saved.Changed += OnSavedChanged;

        foreach (var tab in Tabs)
        {
            OnClick(document, $"tab-{tab}", () => ShowTab(document, tab));
        }

        document.GetElementById("method")?.AddEventListener("change", (_, _) => ShowMethod(document));
        OnClick(document, "send", () => Send(document));
        OnClick(document, "save", () => OpenSaveDialog(document));
        OnClick(document, "save-confirm", () => Save(document));
        OnClick(document, "save-cancel", () => CloseSaveDialog(document));
        OnClick(document, "menu", () => ShowSheet(document, true));
        OnClick(document, "sheet-close", () => ShowSheet(document, false));
        OnClick(document, "backdrop", () => ShowSheet(document, false));
        var modal = document.GetElementById("modal")!;
        modal.AddEventListener("click", (_, ev) =>
        {
            // a click beside the dialog dismisses it
            if (ReferenceEquals(ev.OriginalTarget, modal))
            {
                CloseSaveDialog(document);
            }
        });
        OnEnter(document, "url", () => Send(document));
        OnEnter(document, "name", () => Save(document));
        document.AddEventListener("keydown", (_, ev) =>
        {
            if (ev is KeyboardEvent { Key: "Escape" or "Esc" } && (CloseSaveDialog(document) | ShowSheet(document, false)))
            {
                ev.Cancel();
            }
        });
        document.GetElementById("saved")?.AddEventListener("click", (_, ev) =>
        {
            // one listener for the whole list, which is replaced whenever it changes
            if (ev.OriginalTarget is IElement target && target.Closest("[data-name]") is { } item && item.GetAttribute("data-name") is { } name)
            {
                if (target.Closest(".remove") is not null)
                {
                    Delete(document, name);
                }
                else
                {
                    Restore(document, name);
                    ShowSheet(document, false);
                }
            }
        });
    }

    private void Send(IDocument document)
    {
        if (_sending is { } sending)
        {
            sending.Cancel();
            return;
        }

        if (!TryCreateRequest(document, out var request, out var requester, out var problem))
        {
            ShowStatus(document, problem, "invalid");
            return;
        }

        var cancellation = new CancellationTokenSource();
        var useCookies = IsChecked(document, "cookies");
        _sending = cancellation;
        SetSending(document, true);
        ShowStatus(document, $"Sending with {GetName(requester)}...", null);
        _ = Task.Run(() => SendAsync(document, requester, request, useCookies, cancellation));
    }

    /// <summary>
    /// Asks for the name to save what the form shows under: the one of the request restored
    /// last, else one made of the method and address.
    /// </summary>
    private void OpenSaveDialog(IDocument document)
    {
        var url = GetValue(document, "url").Trim();

        if (url.Length == 0)
        {
            ShowStatus(document, "Enter the address of the request to save it.", "invalid", "save-status");
            return;
        }

        ShowSheet(document, false);
        SetValue(document, "name", _active ?? $"{GetSelected(document, "method").ToUpperInvariant()} {GetShortAddress(url)}");
        document.GetElementById("modal")!.ClassName = "modal open";
        (document.GetElementById("name") as IHtmlElement)?.DoFocus();
    }

    /// <summary>
    /// Closes the dialog asking for the name.
    /// </summary>
    /// <returns>If it was open.</returns>
    private static bool CloseSaveDialog(IDocument document)
    {
        var modal = document.GetElementById("modal")!;
        var wasOpen = modal.ClassList.Contains("open");
        modal.ClassName = "modal";
        return wasOpen;
    }

    /// <summary>
    /// Opens or closes the sheet with the saved requests.
    /// </summary>
    /// <returns>If it changed.</returns>
    private static bool ShowSheet(IDocument document, bool isOpen)
    {
        var sheet = document.GetElementById("sheet")!;
        var wasOpen = sheet.ClassList.Contains("open");
        sheet.ClassName = isOpen ? "sheet open" : "sheet";
        document.GetElementById("backdrop")!.ClassName = isOpen ? "backdrop open" : "backdrop";
        return wasOpen != isOpen;
    }

    /// <summary>
    /// Saves what the form shows under the name the dialog shows (or one made of the method
    /// and address).
    /// </summary>
    private void Save(IDocument document)
    {
        var url = GetValue(document, "url").Trim();

        if (url.Length == 0)
        {
            CloseSaveDialog(document);
            ShowStatus(document, "Enter the address of the request to save it.", "invalid", "save-status");
            return;
        }

        var method = GetSelected(document, "method");
        var name = GetValue(document, "name").Trim();

        if (name.Length == 0)
        {
            name = $"{method.ToUpperInvariant()} {GetShortAddress(url)}";
        }

        var choice = GetSelected(document, "requester");
        var requester = int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < _requesters.Length
            ? GetName(_requesters[index])
            : null;
        var replaces = saved.Find(name) is not null;
        _active = name;
        saved.Save(new SavedRequest(
            name,
            method,
            url,
            GetValue(document, "headers"),
            GetValue(document, "body"),
            requester,
            IsChecked(document, "cookies")));
        CloseSaveDialog(document);
        ShowStatus(document, replaces ? $"Updated \"{name}\"." : $"Saved \"{name}\".", "saved", "save-status");
    }

    /// <summary>
    /// Fills the form with the saved request of the given name.
    /// </summary>
    private void Restore(IDocument document, string name)
    {
        if (saved.Find(name) is not { } request)
        {
            return;
        }

        var requester = Array.FindIndex(_requesters, m => GetName(m) == request.Requester);
        SetSelected(document, "method", Methods.Any(m => m.ToString() == request.Method) ? request.Method : nameof(HttpMethod.Get));
        SetSelected(document, "requester", requester >= 0 ? requester.ToString(CultureInfo.InvariantCulture) : "auto");
        SetValue(document, "url", request.Url);
        SetValue(document, "headers", request.Headers);
        SetValue(document, "body", request.Body);
        SetChecked(document, "cookies", request.UseCookies);
        ShowMethod(document);
        _active = request.Name;
        ShowSaved(document);
        ShowStatus(document, request.Requester is not null && requester < 0
            ? $"Restored \"{request.Name}\", but its requester ({request.Requester}) is not there: it goes to the automatic one."
            : $"Restored \"{request.Name}\".", null);
    }

    private void Delete(IDocument document, string name)
    {
        if (saved.Remove(name))
        {
            if (string.Equals(_active, name, StringComparison.OrdinalIgnoreCase))
            {
                _active = null;
            }

            ShowStatus(document, $"Deleted \"{name}\".", null);
        }
    }

    /// <summary>
    /// Raised on any thread: the list is drawn again on the event loop.
    /// </summary>
    private void OnSavedChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed && _document is { } document)
        {
            try
            {
                _ = Engine.InvokeAsync(_ => ShowSaved(document));
            }
            catch (ObjectDisposedException)
            {
                // the window was closed meanwhile
            }
        }
    }

    private void ShowSaved(IDocument document)
    {
        var requests = saved.All;
        var items = new StringBuilder();

        foreach (var request in requests)
        {
            var method = request.Method.ToUpperInvariant();
            var active = string.Equals(request.Name, _active, StringComparison.OrdinalIgnoreCase) ? " active" : string.Empty;
            items.Append($"""<div class="item{active}" data-name="{Encode(request.Name)}" title="{Encode(request.Url)}">""");
            items.Append($"""<span class="verb {method.ToLowerInvariant()}">{Encode(method)}</span>""");
            items.Append($"""<span class="text"><span class="name">{Encode(request.Name)}</span><span class="url">{Encode(GetShortAddress(request.Url))}</span></span>""");
            items.Append("""<button type="button" class="remove" title="Delete">×</button></div>""");
        }

        document.GetElementById("saved")!.InnerHtml = items.Length > 0
            ? items.ToString()
            : """<div class="none">Save a request with the button below its response to find it here.</div>""";
        var count = requests.Count.ToString(CultureInfo.InvariantCulture);
        document.GetElementById("saved-count")!.TextContent = count;
        var badge = document.GetElementById("menu-count")!;
        badge.TextContent = count;
        badge.ClassName = requests.Count > 0 ? "badge" : "badge empty";
    }

    /// <summary>
    /// Colors the method's field like its badges in the list.
    /// </summary>
    private static void ShowMethod(IDocument document)
    {
        if (document.GetElementById("method") is { } method)
        {
            method.ClassName = $"method {GetSelected(document, "method").ToLowerInvariant()}";
        }
    }

    /// <summary>
    /// Runs the handler when Enter is pressed in the field with the given id.
    /// </summary>
    private static void OnEnter(IDocument document, string id, Action handler) =>
        document.GetElementById(id)?.AddEventListener("keydown", (_, ev) =>
        {
            if (ev is KeyboardEvent { Key: "Enter" })
            {
                ev.Cancel();
                handler();
            }
        });

    /// <summary>
    /// Runs on any thread: sends the request and shows what came back on the event loop.
    /// </summary>
    private async Task SendAsync(IDocument document, IRequester requester, Request request, bool useCookies, CancellationTokenSource cancellation)
    {
        Action<IDocument?> show;
        var cookies = Engine.Context.GetService<ICookieProvider>();

        try
        {
            if (useCookies && cookies?.GetCookie(request.Address) is { Length: > 0 } cookie && !request.Headers.ContainsKey(HeaderNames.Cookie))
            {
                request.Headers[HeaderNames.Cookie] = cookie;
            }

            var watch = Stopwatch.StartNew();
            using var response = await requester.RequestAsync(request, cancellation.Token);

            if (response is null)
            {
                show = _ => ShowStatus(document, $"{GetName(requester)} gave no response.", "invalid");
            }
            else
            {
                var firstByte = watch.Elapsed;
                var body = await ReadAsync(response, cancellation.Token);
                var total = watch.Elapsed;

                if (useCookies && cookies is not null && response.Headers.TryGetValue(HeaderNames.SetCookie, out var setCookie))
                {
                    cookies.SetCookie(response.Address ?? request.Address, setCookie);
                }

                // the response goes away with this method
                var result = new Result(
                    request,
                    response.StatusCode,
                    response.Address?.Href ?? request.Address.Href,
                    new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase),
                    body,
                    firstByte,
                    total);
                show = _ => ShowResult(document, requester, result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            show = _ => ShowStatus(document, "Cancelled.", null);
        }
        catch (Exception ex)
        {
            show = _ => ShowStatus(document, $"The request failed: {ex.GetBaseException().Message}", "invalid");
        }

        if (_isDisposed)
        {
            cancellation.Dispose();
            return;
        }

        try
        {
            await Engine.InvokeAsync(d =>
            {
                _sending = null;
                SetSending(document, false);
                show(d);
            });
        }
        catch (ObjectDisposedException)
        {
            // the window was closed meanwhile
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private bool TryCreateRequest(IDocument document, out Request request, out IRequester requester, out string problem)
    {
        request = new Request();
        requester = null!;
        problem = string.Empty;
        var address = GetValue(document, "url").Trim();

        if (address.Length == 0)
        {
            problem = "Enter the address to send the request to.";
            return false;
        }

        var url = new Url(BrowserEngine.NormalizeAddress(address, Engine.Schemes));

        if (url.IsInvalid)
        {
            problem = $"{address} is not a valid address.";
            return false;
        }

        var method = Methods.FirstOrDefault(m => m.ToString() == GetSelected(document, "method"));
        var body = GetValue(document, "body");

        if (body.Length > 0 && method is HttpMethod.Get or HttpMethod.Head or HttpMethod.Trace)
        {
            problem = $"A {method.ToString().ToUpperInvariant()} request has no body: clear it, or choose another method.";
            return false;
        }

        if (!TryParseHeaders(GetValue(document, "headers"), out var headers, out problem))
        {
            return false;
        }

        var choice = GetSelected(document, "requester");

        if (int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < _requesters.Length)
        {
            requester = _requesters[index];

            if (!requester.SupportsProtocol(url.Scheme))
            {
                problem = $"{GetName(requester)} does not support {url.Scheme}: addresses.";
                return false;
            }
        }
        else if (_requesters.FirstOrDefault(m => m.SupportsProtocol(url.Scheme)) is { } supporting)
        {
            requester = supporting;
        }
        else
        {
            problem = $"No requester supports {url.Scheme}: addresses.";
            return false;
        }

        request.Address = url;
        request.Method = method;

        foreach (var header in headers)
        {
            request.Headers[header.Key] = header.Value;
        }

        if (body.Length > 0)
        {
            if (!request.Headers.ContainsKey(HeaderNames.ContentType))
            {
                var trimmed = body.TrimStart();
                request.Headers[HeaderNames.ContentType] = trimmed.StartsWith('{') || trimmed.StartsWith('[')
                    ? "application/json; charset=utf-8"
                    : "text/plain; charset=utf-8";
            }

            request.Content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }

        return true;
    }

    private static async Task<Body> ReadAsync(IResponse response, CancellationToken cancellationToken)
    {
        if (response.Content is not { } content)
        {
            return new Body([], false);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            var count = Math.Min(read, MaxBodyBytes - (int)buffer.Length);
            buffer.Write(chunk, 0, count);

            if (count < read)
            {
                return new Body(buffer.ToArray(), true);
            }
        }

        return new Body(buffer.ToArray(), false);
    }

    private static void ShowResult(IDocument document, IRequester requester, Result result)
    {
        var code = (int)result.StatusCode;
        var status = document.GetElementById("response-status")!;
        status.TextContent = $"{code} {result.StatusCode}";
        status.ClassName = code switch
        {
            < 300 => "code success",
            < 400 => "code redirect",
            _ => "code failure",
        };
        var size = FormatSize(result.Body.Data.Length);
        document.GetElementById("response-time")!.TextContent = FormatTime(result.Total);
        document.GetElementById("response-first-byte")!.TextContent = FormatTime(result.FirstByte);
        document.GetElementById("response-size")!.TextContent = result.Body.IsTruncated ? $"> {size}" : size;
        document.GetElementById("response-requester")!.TextContent = GetName(requester);
        document.GetElementById("response-address")!.TextContent = result.Address;

        result.Headers.TryGetValue(HeaderNames.ContentType, out var contentType);
        document.GetElementById("body-text")!.TextContent = FormatBody(result.Body, contentType);
        document.GetElementById("headers-list")!.InnerHtml = FormatHeaders(result.Headers);
        document.GetElementById("request-line")!.TextContent =
            $"{result.Request.Method.ToString().ToUpperInvariant()} {result.Request.Address.Href}";
        // what was sent: the engine adds its user agent and default headers to every request
        document.GetElementById("request-headers")!.InnerHtml = FormatHeaders(result.Request.Headers);
        document.GetElementById("tab-headers")!.QuerySelector(".badge")!.TextContent =
            result.Headers.Count.ToString(CultureInfo.InvariantCulture);
        document.GetElementById("response-empty")!.ClassName = "placeholder hidden";
        document.GetElementById("response-content")!.ClassName = "content";
        ShowStatus(document, string.Empty, null);
    }

    private static string FormatBody(Body body, string? contentType)
    {
        if (body.Data.Length == 0)
        {
            return "(no content)";
        }

        MediaTypeHeaderValue.TryParse(contentType, out var mediaType);
        var type = mediaType?.MediaType?.ToLowerInvariant() ?? string.Empty;

        if (IsBinary(type, body.Data))
        {
            return $"({FormatSize(body.Data.Length)} of {(type.Length > 0 ? type : "binary data")}, which is not shown)";
        }

        var text = GetEncoding(mediaType?.CharSet).GetString(body.Data);

        if (!body.IsTruncated && type.EndsWith("json", StringComparison.Ordinal))
        {
            text = Indent(text);
        }

        if (text.Length > MaxShownCharacters)
        {
            return $"{text[..MaxShownCharacters]}\n\n(the rest of the {text.Length:N0} characters is not shown)";
        }

        return body.IsTruncated ? $"{text}\n\n(the rest of the response was not read)" : text;
    }

    private static bool IsBinary(string type, byte[] data)
    {
        if (type.StartsWith("image/", StringComparison.Ordinal) && !type.Contains("svg", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.StartsWith("audio/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal) ||
            type.StartsWith("font/", StringComparison.Ordinal) ||
            type is "application/octet-stream" or "application/pdf" or "application/zip" or "application/gzip" or "application/wasm")
        {
            return true;
        }

        // text has no null characters
        return Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 4096)) >= 0;
    }

    private static Encoding GetEncoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string Indent(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string FormatHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var rows = string.Concat(headers
            .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"""<div class="header"><span class="name">{Encode(m.Key)}</span><span class="value">{Encode(m.Value)}</span></div>"""));
        return rows.Length > 0 ? rows : """<div class="empty">No headers.</div>""";
    }

    private static string FormatTime(TimeSpan time) => time.TotalSeconds >= 1
        ? $"{time.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s"
        : $"{time.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
    };

    /// <summary>
    /// The name of a requester: its type, without the "Requester" all of them end with.
    /// </summary>
    private static string GetName(IRequester requester)
    {
        var name = requester.GetType().Name;
        return name.Length > "Requester".Length && name.EndsWith("Requester", StringComparison.Ordinal) ? name[..^"Requester".Length] : name;
    }

    /// <summary>
    /// The address without its scheme (if it is one of the web), e.g., "example.org/api".
    /// </summary>
    private static string GetShortAddress(string url)
    {
        foreach (var prefix in (string[])["https://", "http://"])
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return url[prefix.Length..].TrimEnd('/');
            }
        }

        return url;
    }

    /// <summary>
    /// Chooses the option of a select. The renderer paints controls from their markup, so the
    /// selected attribute changes along with the live value.
    /// </summary>
    private static void SetSelected(IDocument document, string id, string value)
    {
        if (document.GetElementById(id) is IHtmlSelectElement select)
        {
            foreach (var option in select.Options)
            {
                if (option.Value == value)
                {
                    option.SetAttribute("selected", string.Empty);
                }
                else
                {
                    option.RemoveAttribute("selected");
                }
            }

            select.Value = value;
        }
    }

    private static string GetSelected(IDocument document, string id) =>
        document.GetElementById(id) is IHtmlSelectElement select ? select.Value ?? string.Empty : string.Empty;

    private static void ShowTab(IDocument document, string name)
    {
        foreach (var tab in Tabs)
        {
            document.GetElementById($"tab-{tab}")!.ClassName = tab == name ? "tab active" : "tab";
            document.GetElementById($"panel-{tab}")!.ClassName = tab == name ? "panel active" : "panel";
        }
    }

    private static void SetSending(IDocument document, bool isSending)
    {
        var send = document.GetElementById("send")!;
        send.TextContent = isSending ? "Cancel" : "Send";
        send.ClassName = isSending ? "button" : "button primary";
    }

    private static void ShowStatus(IDocument document, string text, string? kind, string id = "status")
    {
        var status = document.GetElementById(id)!;
        status.TextContent = text;
        status.ClassName = kind is null ? "status" : $"status {kind}";
    }

    private sealed record Body(byte[] Data, bool IsTruncated);

    private sealed record Result(
        Request Request,
        HttpStatusCode StatusCode,
        string Address,
        Dictionary<string, string> Headers,
        Body Body,
        TimeSpan FirstByte,
        TimeSpan Total);
}
