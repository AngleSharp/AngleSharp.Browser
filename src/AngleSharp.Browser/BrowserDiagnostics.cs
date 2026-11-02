namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AngleSharp.Io;
using AngleSharp.Js;

/// <summary>
/// A request made for the current page, with its response once it arrived.
/// </summary>
/// <param name="Method">The HTTP method, e.g., GET.</param>
/// <param name="Url">The requested address.</param>
/// <param name="StartedAt">When the request was sent.</param>
/// <param name="Duration">How long the response took, or null while pending.</param>
/// <param name="StatusCode">The status of the response, or null while pending.</param>
/// <param name="ContentType">The content type of the response, if any.</param>
/// <param name="ContentLength">The announced length of the response in bytes, if any.</param>
public sealed record NetworkEntry(
    string Method,
    string Url,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    HttpStatusCode? StatusCode,
    string? ContentType,
    long? ContentLength)
{
    /// <summary>
    /// Gets if the response has not arrived (yet).
    /// </summary>
    public bool IsPending => StatusCode is null;
}

/// <summary>
/// The kind of a <see cref="DiagnosticMessage"/>.
/// </summary>
public enum DiagnosticKind
{
    /// <summary>
    /// Output of the page's console.
    /// </summary>
    Console,
    /// <summary>
    /// A failed operation, e.g., a script throwing or a navigation failing.
    /// </summary>
    Error,
}

/// <summary>
/// A message reported while showing the current page.
/// </summary>
public sealed record DiagnosticMessage(DiagnosticKind Kind, DateTimeOffset Time, string Text);

/// <summary>
/// Collects what happens while showing the current page: requests, errors and console output,
/// like the developer tools of a browser. Starts over with every page. Thread-safe; the
/// <see cref="Changed"/> event may be raised from any thread.
/// </summary>
public sealed class BrowserDiagnostics
{
    private const int MaxEntries = 1000;

    private readonly object _lock = new();
    private readonly List<NetworkEntry> _requests = [];
    private readonly Dictionary<Request, NetworkEntry> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly List<DiagnosticMessage> _messages = [];
    private DateTimeOffset _navigationStart = DateTimeOffset.MinValue;

    internal BrowserDiagnostics()
    {
    }

    /// <summary>
    /// Raised when an entry was added or changed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the requests of the current page, in the order they were sent.
    /// </summary>
    public IReadOnlyList<NetworkEntry> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the errors and the console output of the current page, oldest first.
    /// </summary>
    public IReadOnlyList<DiagnosticMessage> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>
    /// Forgets everything collected so far.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _requests.Clear();
            _pending.Clear();
            _messages.Clear();
        }

        OnChanged();
    }

    internal void OnRequesting(Request request)
    {
        lock (_lock)
        {
            if (_requests.Count >= MaxEntries)
            {
                return;
            }

            var entry = new NetworkEntry(request.Method.ToString().ToUpperInvariant(), request.Address.Href, DateTimeOffset.Now, null, null, null, null);
            _pending[request] = entry;
            _requests.Add(entry);
        }

        OnChanged();
    }

    internal void OnRequested(Request request, IResponse? response)
    {
        lock (_lock)
        {
            // the entry may have been dropped with its page in the meantime
            var index = _pending.Remove(request, out var entry) ? _requests.FindIndex(m => ReferenceEquals(m, entry)) : -1;

            if (index < 0)
            {
                return;
            }

            var headers = response?.Headers;
            var length = headers is not null && headers.TryGetValue(HeaderNames.ContentLength, out var value) && long.TryParse(value, out var bytes) ? bytes : (long?)null;
            _requests[index] = entry! with
            {
                Duration = DateTimeOffset.Now - entry.StartedAt,
                // a requester answering nothing failed to get a response at all
                StatusCode = response?.StatusCode ?? 0,
                ContentType = headers is not null && headers.TryGetValue(HeaderNames.ContentType, out var type) ? type : null,
                ContentLength = length,
            };
        }

        OnChanged();
    }

    internal void OnMessage(DiagnosticKind kind, string text)
    {
        lock (_lock)
        {
            if (_messages.Count >= MaxEntries)
            {
                _messages.RemoveAt(0);
            }

            _messages.Add(new DiagnosticMessage(kind, DateTimeOffset.Now, text));
        }

        OnChanged();
    }

    /// <summary>
    /// Marks the start of a navigation; what happened before belongs to the previous page.
    /// </summary>
    internal void OnNavigating()
    {
        lock (_lock)
        {
            _navigationStart = DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// Drops what belongs to previous pages, once the page with the given address is shown:
    /// everything before its document was requested, else before the navigation started.
    /// </summary>
    internal void OnDocumentChanged(string url)
    {
        lock (_lock)
        {
            var index = _requests.FindLastIndex(m => m.Url == url);
            var start = index >= 0 ? _requests[index].StartedAt : _navigationStart;
            _requests.RemoveAll(m => m.StartedAt < start);
            _messages.RemoveAll(m => m.Time < start);

            foreach (var request in _pending.Where(m => m.Value.StartedAt < start).Select(m => m.Key).ToArray())
            {
                _pending.Remove(request);
            }
        }

        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Forwards the page's console output to the diagnostics.
    /// </summary>
    internal sealed class ConsoleLogger(BrowserDiagnostics diagnostics) : IConsoleLogger
    {
        public void Log(object[] values) =>
            diagnostics.OnMessage(DiagnosticKind.Console, string.Join(" ", values.Select(m => m?.ToString() ?? "null")));
    }
}
