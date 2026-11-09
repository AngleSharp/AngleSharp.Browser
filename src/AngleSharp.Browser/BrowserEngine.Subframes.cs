namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Browser;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;

/// <summary>
/// What is in iframes: their documents have browsing contexts and layouts of their own, so what
/// the host gives in viewport coordinates has to arrive there in theirs.
/// </summary>
partial class BrowserEngine
{
    // the document of an iframe holding the focus, else the page does
    private IDocument? _focusedFrameDocument;

    // what is hovered inside of the iframes around the hovered element, to leave it again
    private readonly List<IDomHarness> _hoveredFrames = [];

    private IDocument? FocusedDocument => _focusedFrameDocument is { } frame && IsInPage(frame) ? frame : _document;

    /// <summary>
    /// Whether the document is the page or in one of its iframes.
    /// </summary>
    private bool IsInPage(IDocument? document)
    {
        for (var current = document; current is not null; current = current.Context?.Parent?.Active)
        {
            if (ReferenceEquals(current, _document))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The iframe showing the document, for one that isn't the page.
    /// </summary>
    private static IHtmlInlineFrameElement? GetFrameElement(IDocument document) =>
        document.Context?.Parent?.Active?.QuerySelectorAll("iframe")
            .OfType<IHtmlInlineFrameElement>()
            .FirstOrDefault(frame => ReferenceEquals(frame.ContentDocument, document));

    /// <summary>
    /// A position in the viewport of the page in the viewport of the document of the element.
    /// </summary>
    private (double X, double Y) ToClient(IElement? element, double x, double y)
    {
        var document = element?.Owner;

        while (document is not null && !ReferenceEquals(document, _document) && GetFrameElement(document) is { } frame)
        {
            var box = frame.GetBoundingClientRect();
            var style = frame.ComputeCurrentStyle();
            x -= box.Left + frame.GetClientLeft() + ParsePixels(style.GetPropertyValue("padding-left"));
            y -= box.Top + frame.GetClientTop() + ParsePixels(style.GetPropertyValue("padding-top"));
            document = frame.Owner;
        }

        return (x, y);
    }

    private static double ParsePixels(string? value) =>
        double.TryParse(value?.Replace("px", string.Empty), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels) ? pixels : 0d;

    /// <summary>
    /// What is at the position in the iframe that was found there, if it is one: the harness of
    /// each document only knows the boxes of that document.
    /// </summary>
    private IElement? HitTestInFrame(IElement? element, double x, double y)
    {
        while (element is IHtmlInlineFrameElement { ContentDocument: { DocumentElement: { } root, Context: { } context } })
        {
            var harness = context.GetDomHarness();
            var inner = element;
            var (innerX, innerY) = ToClient(root, x, y);
            var position = (X: innerX, Y: innerY + root.GetScrollTop());

            // the harness only hit-tests again when the position changes
            harness.MousePosition = harness.MousePosition == position ? (position.X, position.Y + 0.001) : position;
            element = harness.HoveredElement ?? inner;

            if (ReferenceEquals(element, inner))
            {
                break;
            }
        }

        return element;
    }

    /// <summary>
    /// The element is hovered in its document, and the iframes around it in theirs.
    /// </summary>
    private void SetHoveredInDocuments(IElement? element)
    {
        foreach (var previous in _hoveredFrames)
        {
            previous.SetHoveredElement(null);
        }

        _hoveredFrames.Clear();

        var current = element;

        while (current?.Owner is { Context: { } context } document && !ReferenceEquals(document, _document) && GetFrameElement(document) is { } frame)
        {
            var harness = context.GetDomHarness();
            harness.SetHoveredElement(current);
            _hoveredFrames.Add(harness);
            current = frame;
        }

        _harness.SetHoveredElement(current);
    }

    /// <summary>
    /// Follows a link or form of a document in an iframe: navigating the iframe, unless the link
    /// aims at what is around it.
    /// </summary>
    private async Task NavigateInFrameAsync(IDocument document, Func<Task> navigate)
    {
        try
        {
            await navigate().ConfigureAwait(false);
        }
        finally
        {
            KeepAnimating(SettleDuration);
            OnLayoutChanged();
        }
    }

    /// <summary>
    /// The event loop of the page as an iframe sees it: a document going away cancels what it
    /// queued, not what the page and the other iframes did.
    /// </summary>
    private sealed class FrameEventLoop(IEventLoop loop) : IEventLoop
    {
        private readonly List<ICancellable> _queued = [];

        public ICancellable Enqueue(Action<CancellationToken> action, TaskPriority priority)
        {
            var entry = loop.Enqueue(action, priority);

            lock (_queued)
            {
                _queued.RemoveAll(static queued => queued.IsCompleted);
                _queued.Add(entry);
            }

            return entry;
        }

        public void Spin() => loop.Spin();

        public void CancelAll()
        {
            lock (_queued)
            {
                foreach (var queued in _queued)
                {
                    queued.Cancel();
                }

                _queued.Clear();
            }
        }
    }
}
