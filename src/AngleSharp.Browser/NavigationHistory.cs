namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using AngleSharp.Browser.Dom;
using AngleSharp.Dom;
using System.Reflection;

/// <summary>
/// The session history of the engine's top-level browsing context, which scripts reach as
/// <c>window.history</c>. An entry added by <c>pushState</c> belongs to the document of the entry
/// it was added on; moving between entries of one document fires <c>popstate</c> instead of
/// loading anything (HTML 7.4.2).
/// </summary>
sealed class NavigationHistory : IHistory
{
    private readonly List<Entry> _entries = new();
    private int _index = -1;

    /// <summary>
    /// One entry: its address, its document (once known) and the state a script gave it.
    /// </summary>
    public sealed record Entry(string Url, IDocument? Document, object? State);

    /// <summary>
    /// Raised when a script asks to move through the history (back, forward, go).
    /// </summary>
    public event Action<int>? TraversalRequested;

    /// <summary>
    /// Raised when a script changed the current entry (pushState, replaceState).
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets or sets if the engine is loading the document of an entry it moved to, which then
    /// takes that entry instead of adding one.
    /// </summary>
    public bool IsTraversing { get; set; }

    public string? Current
    {
        get { lock (_entries) return _index >= 0 ? _entries[_index].Url : null; }
    }

    public bool CanGoBack
    {
        get { lock (_entries) return _index > 0; }
    }

    public bool CanGoForward
    {
        get { lock (_entries) return _index < _entries.Count - 1; }
    }

    /// <summary>
    /// The address of the current entry, if it belongs to the given (displayed) document.
    /// </summary>
    public string? GetUrl(IDocument? document)
    {
        lock (_entries)
        {
            return _index >= 0 && document is not null && ReferenceEquals(_entries[_index].Document, document)
                ? _entries[_index].Url
                : null;
        }
    }

    /// <summary>
    /// Records a document the engine displays: a new entry, unless it is the current one.
    /// </summary>
    public void Push(string url, IDocument document)
    {
        lock (_entries)
        {
            if (IsTraversing || (_index >= 0 && (ReferenceEquals(_entries[_index].Document, document) || (_entries[_index].Document is null && _entries[_index].Url == url))))
            {
                if (_index >= 0)
                {
                    _entries[_index] = _entries[_index] with { Document = document };
                }

                return;
            }

            Add(new Entry(url, document, null));
        }
    }

    /// <summary>
    /// Moves to the entry <paramref name="delta"/> steps away, if any.
    /// </summary>
    /// <param name="delta">The number of steps (negative for back).</param>
    /// <param name="isSameDocument">If the entry belongs to the document of the current one.</param>
    /// <returns>The entry moved to, or null.</returns>
    public Entry? Move(int delta, out bool isSameDocument)
    {
        lock (_entries)
        {
            var target = _index + delta;
            isSameDocument = false;

            if (delta == 0 || _index < 0 || target < 0 || target >= _entries.Count)
            {
                return null;
            }

            var current = _entries[_index];
            _index = target;
            isSameDocument = current.Document is not null && ReferenceEquals(current.Document, _entries[target].Document);
            return _entries[target];
        }
    }

    public int Length
    {
        get { lock (_entries) return _entries.Count; }
    }

    public int Index
    {
        get { lock (_entries) return _index; }
    }

    public IDocument this[int index]
    {
        get { lock (_entries) return _entries[index].Document!; }
    }

    public object State
    {
        get { lock (_entries) return _index >= 0 ? _entries[_index].State! : null!; }
    }

    public void Go(int delta = 0) => TraversalRequested?.Invoke(delta);

    public void Back() => Go(-1);

    public void Forward() => Go(1);

    public void PushState(object data, string? title, string? url = null)
    {
        // AngleSharp records every document it navigates to like this
        if (data is IDocument document)
        {
            Push(url ?? document.Url, document);
            return;
        }

        lock (_entries)
        {
            if (_index < 0)
            {
                throw new DomException(DomError.InvalidState);
            }

            var current = _entries[_index];
            Add(new Entry(ResolveUrl(current, url), current.Document, data));
            SetDocumentUrl(_entries[_index]);
        }

        Changed?.Invoke();
    }

    public void ReplaceState(object data, string? title, string? url = null)
    {
        lock (_entries)
        {
            if (_index < 0)
            {
                throw new DomException(DomError.InvalidState);
            }

            var current = _entries[_index];
            _entries[_index] = current with { Url = ResolveUrl(current, url), State = data };
            SetDocumentUrl(_entries[_index]);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Gives the document the address of its entry without navigating (HTML 7.4.4), as its
    /// location, document.URL and relative links have to follow pushState. Core has that only
    /// as a protected setter so far.
    /// </summary>
    public static void SetDocumentUrl(Entry entry)
    {
        if (entry.Document is null || entry.Document.Url == entry.Url)
        {
            return;
        }

        for (var type = entry.Document.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(nameof(IDocument.DocumentUri), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var setter = property?.GetSetMethod(true);

            if (setter is not null)
            {
                setter.Invoke(entry.Document, new object[] { entry.Url });
                return;
            }
        }
    }

    private void Add(Entry entry)
    {
        _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry);
        _index = _entries.Count - 1;
    }

    // a script may only change the address within the origin of the document
    private static string ResolveUrl(Entry current, string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return current.Url;
        }

        var baseUrl = new Url(current.Url);
        var target = new Url(baseUrl, url);

        if (target.IsInvalid || !string.Equals(target.Origin, baseUrl.Origin, StringComparison.Ordinal))
        {
            throw new DomException(DomError.Security);
        }

        return target.Href;
    }
}
