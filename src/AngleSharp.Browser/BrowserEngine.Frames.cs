namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

partial class BrowserEngine
{
    private PointerFrame? _pointerFrame;
    private IDocument? _paintedDocument;
    private (RenderedFrame Frame, IDocument Document, int Width, int Height, int LayoutVersion)? _lastFrame;
    // the latest frame the harness laid out or patched, whose metrics are reported once
    private RenderedFrame? _lastLaidOutFrame;
    // anything but scrolling the page invalidates the last frame's layout
    private volatile bool _needsLayout = true;
    private volatile bool _needsFullLayout = true;
    // if CSS transitions or animations ran in the last frame, i.e., time changes how it looks
    private volatile bool _isLastFrameAnimating;
    private bool _isSyncingScroll;
    private volatile bool _isPageScrollLocked;

    /// <summary>
    /// What the last frame painted where, together with what the pointer would show for each
    /// element. Immutable, so the host's thread reads it without waiting for the engine.
    /// </summary>
    private sealed record PointerFrame(IDocument Document, RenderedFrame Frame, HitTestMap Map, Dictionary<IElement, PointerInfo> Infos, double ScrollTop, bool IsScrollShiftable, int LayoutVersion);

    private readonly record struct PointerInfo(string? Href, bool IsTextEntry, bool IsListBox);

    private readonly record struct PointerTarget(IElement? Element, string? Href, BrowserCursor Cursor, bool IsInScrollContainer);

    /// <summary>
    /// Gets if the current document has been painted at least once, i.e., if it is visible.
    /// </summary>
    public bool HasPaintedDocument
    {
        get
        {
            var document = _document;
            return document is null || ReferenceEquals(Volatile.Read(ref _paintedDocument), document);
        }
    }

    /// <summary>
    /// Lays out the current document into a frame, which also refreshes what hit testing
    /// (hover, cursor, links) and scrolling know about the page. The frame is rasterized by
    /// the caller, possibly on another thread while the engine lays out the next one.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    /// <returns>The frame or null if no document is ready to be painted.</returns>
    public async Task<BrowserFrame?> PrepareFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // while the page is busy, the scrolling goes on with the last frame
        while (!await _gate.WaitAsync(ScrollTickInterval, cancellationToken).ConfigureAwait(false))
        {
            if (RepositionShownFrame() is { } frame)
            {
                return frame;
            }
        }

        return await RunEnteredAsync(PrepareFrame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like a compositor, keeps scrolling while the page is busy (e.g., a script laying out the
    /// page over and over): the last frame is moved to the scroll position, which does not touch
    /// the DOM. Returns null if the frame shows the position already or cannot be moved.
    /// </summary>
    private BrowserFrame? RepositionShownFrame()
    {
        var shown = _pointerFrame;
        var document = _document;
        var position = ScrollPosition;
        var time = DateTimeOffset.Now;
        var start = Stopwatch.GetTimestamp();

        if (shown is not { Frame.CanReposition: true } || !ReferenceEquals(shown.Document, document) || shown.ScrollTop == position)
        {
            return null;
        }

        var frame = shown.Frame.Reposition(position);

        // a frame laid out meanwhile is newer than the moved one
        if (Interlocked.CompareExchange(ref _pointerFrame, shown with { Frame = frame, Map = frame.HitTestMap, ScrollTop = frame.ScrollTop }, shown) != shown)
        {
            return null;
        }

        // the page changed since, which the next frame shows once the page is free again
        if (_needsLayout)
        {
            Invalidated?.Invoke(this, EventArgs.Empty);
        }

        var layout = Stopwatch.GetElapsedTime(start);
        return new BrowserFrame(frame, raster => OnFramePainted(document, new FrameTiming(time, FrameKind.Reposition, layout, raster)));
    }

    private BrowserFrame? PrepareFrame()
    {
        SyncActiveDocument();

        if (!IsReadyForLayout())
        {
            return null;
        }

        var document = _document!;
        var version = Volatile.Read(ref _layoutVersion);
        var time = DateTimeOffset.Now;
        var start = Stopwatch.GetTimestamp();
        var kind = FrameKind.Layout;
        var metrics = RenderMetrics.None;
        RenderedFrame frame;

        // a page scrolling as a whole is laid out once more with its overscan before frames are reused
        if (!_needsLayout && _lastFrame is { Frame.CanReposition: true } last && (last.Frame.OverscanTop > 0 || !last.Frame.IsScrollShiftable) &&
            ReferenceEquals(last.Document, document) &&
            last.Width == ViewportWidth && last.Height == ViewportHeight && last.LayoutVersion == version)
        {
            // only the page scrolled: the last frame's display list covers all of it already, and
            // fixed boxes stay in place
            frame = last.Frame.Reposition(ScrollTop);
            kind = FrameKind.Reposition;
        }
        else if (!_needsFullLayout && TryReuseLastFrame(document, out var patched) is { } reused)
        {
            _needsLayout = false;
            frame = reused;
            kind = FrameKind.Update;
            metrics = patched;
        }
        else
        {
            _needsLayout = false;
            _needsFullLayout = false;
            _isPainting = true;

            try
            {
                AdvanceClock();
                // a page shows up once what is in view is laid out, the next frame lays out all of it
                frame = _lastFrame is { } previous && ReferenceEquals(previous.Document, document)
                    ? _harness.PrepareFrame(ViewportHeight / 2)
                    : _harness.PrepareViewportFrame(ViewportHeight / 2);
                _lastLaidOutFrame = frame;
                metrics = frame.Metrics;
                UpdatePageScrollLock(document);
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

        _lastFrame = (frame, document, ViewportWidth, ViewportHeight, version);

        _pointerFrame = new PointerFrame(document, frame, frame.HitTestMap, CreatePointerInfos(frame.HitTestMap), frame.ScrollTop, frame.IsScrollShiftable, version);
        // the page goes on below what a frame of the viewport only shows (it is animating)
        if (!frame.IsPartial)
        {
            UpdateScrollExtent(frame.ContentHeight, version);
        }

        AdoptScrollPosition(frame.ScrollTop);
        // the content below the pointer may have moved; like other browsers, the hover state
        // waits until scrolling ends, as each change of it needs a layout
        if (!IsScrolling)
        {
            UpdateHover(_lastPointer.X, _lastPointer.Y, isMove: false);
        }

        _isLastFrameAnimating = frame.IsAnimating;

        // the first frame of a page tells if it scrolls as a whole, then it gets an overscan
        if (frame.IsAnimating || (frame is { IsScrollShiftable: true, OverscanTop: 0 } && ViewportHeight > 1))
        {
            Invalidate();
        }

        // the list of an open dropdown is on top of the frame
        var shown = RefreshSelectPopup() is { } popup ? frame.WithOverlay(popup.Overlay) : frame;
        var layout = Stopwatch.GetElapsedTime(start);
        return new BrowserFrame(shown, raster => OnFramePainted(document, new FrameTiming(time, kind, layout, raster) { Metrics = metrics }));
    }

    /// <summary>
    /// The last frame brought up to date without a layout of the whole document, if the changes
    /// allow it (see <c>IDomHarness.UpdateFrame</c>): it is the same frame when they did
    /// not change what it shows (e.g. a script showing, measuring and hiding a dropdown again on
    /// every scroll), else the boxes out of the flow that changed are replaced in it (e.g. a menu
    /// changing its items). Requires exclusive access.
    /// </summary>
    private RenderedFrame? TryReuseLastFrame(IDocument document, out RenderMetrics metrics)
    {
        metrics = RenderMetrics.None;

        if (_lastFrame is not { } last || !ReferenceEquals(last.Document, document) || last.Width != ViewportWidth || last.Height != ViewportHeight ||
            _harness.UpdateFrame() is not { } frame)
        {
            return null;
        }

        // the frame the harness had already, or one with boxes laid out since
        if (!ReferenceEquals(frame, _lastLaidOutFrame))
        {
            _lastLaidOutFrame = frame;
            metrics = frame.Metrics;
        }

        var scrollTop = ScrollTop;

        if (frame.ScrollTop == scrollTop)
        {
            return frame;
        }

        return frame.CanReposition && (frame.OverscanTop > 0 || !frame.IsScrollShiftable) ? frame.Reposition(scrollTop) : null;
    }

    /// <summary>
    /// A page with overflow: hidden on its root (or, as that is propagated, on its body) cannot
    /// be scrolled by the user, while it stays where it is, e.g., for the time a dialog is open.
    /// </summary>
    private void UpdatePageScrollLock(IDocument document)
    {
        var overflow = document.DocumentElement?.ComputeCurrentStyle().GetPropertyValue("overflow-y");

        if (string.IsNullOrEmpty(overflow) || overflow == "visible")
        {
            overflow = document.Body?.ComputeCurrentStyle().GetPropertyValue("overflow-y");
        }

        _isPageScrollLocked = overflow is "hidden" or "clip";
    }

    private void AdvanceClock()
    {
        var now = Environment.TickCount64;

        if (_lastPaint > 0)
        {
            _harness.AdvanceTime(TimeSpan.FromMilliseconds(now - _lastPaint));
        }

        _lastPaint = now;
    }

    private void OnFramePainted(IDocument document, FrameTiming timing)
    {
        _diagnostics.OnFrame(timing);

        if (!ReferenceEquals(Interlocked.Exchange(ref _paintedDocument, document), document))
        {
            OnStateChanged();
        }
    }

    private static Dictionary<IElement, PointerInfo> CreatePointerInfos(HitTestMap map)
    {
        var infos = new Dictionary<IElement, PointerInfo>(ReferenceEqualityComparer.Instance);
        var links = new Dictionary<IElement, IElement?>(ReferenceEqualityComparer.Instance);

        foreach (var element in map.Elements)
        {
            var link = FindLink(element, links);
            infos[element] = new PointerInfo((link as IUrlUtilities)?.Href, FormControls.IsTextEntry(element), element.IsListBox());
        }

        return infos;
    }

    // links are shared by the boxes of their content, so each element is only looked at once
    private static IElement? FindLink(IElement? element, Dictionary<IElement, IElement?> links)
    {
        if (element is null)
        {
            return null;
        }

        if (!links.TryGetValue(element, out var link))
        {
            link = element is IHtmlAnchorElement or IHtmlAreaElement && element.HasAttribute("href")
                ? element
                : FindLink(element.ParentElement, links);
            links[element] = link;
        }

        return link;
    }

    /// <summary>
    /// Looks up the given viewport position in the last frame, as the host currently shows it.
    /// Does not touch the DOM, so it can be called from any thread. Returns null without a
    /// frame of the current document.
    /// </summary>
    private PointerTarget? FindPointerTarget(double x, double y)
    {
        var frame = _pointerFrame;

        if (frame is null || !ReferenceEquals(frame.Document, _document))
        {
            return null;
        }

        // a shiftable frame is shown shifted by the scrolling done since it was painted
        var frameY = frame.IsScrollShiftable ? y + ScrollPosition - frame.ScrollTop : y;

        if (frame.Map.HitTest(x, frameY) is not { } hit || !IsInPage(hit.Element.Owner))
        {
            return new PointerTarget(null, null, BrowserCursor.Default, false);
        }

        var info = frame.Infos.GetValueOrDefault(hit.Element);
        var cursor = hit.Cursor switch
        {
            "pointer" => BrowserCursor.Pointer,
            "text" => BrowserCursor.Text,
            _ when info.Href is not null => BrowserCursor.Pointer,
            _ when info.IsTextEntry => BrowserCursor.Text,
            _ => BrowserCursor.Default,
        };
        return new PointerTarget(hit.Element, info.Href, cursor, hit.IsInScrollContainer || info.IsListBox);
    }

    /// <summary>
    /// Gets if the last frame shows the current layout, such that hit testing it is exact.
    /// </summary>
    private bool IsPointerFrameCurrent()
    {
        var frame = _pointerFrame;
        return frame is not null &&
            ReferenceEquals(frame.Document, _document) &&
            frame.LayoutVersion == Volatile.Read(ref _layoutVersion) &&
            (frame.IsScrollShiftable || frame.ScrollTop == ScrollTop);
    }
}
