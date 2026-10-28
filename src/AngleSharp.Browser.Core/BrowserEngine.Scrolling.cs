namespace AngleSharp.Browser;

using System;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;

/// <summary>
/// Smooth page scrolling. The scroll position moves on its own clock, independent of the DOM:
/// hosts show it right away by shifting the last frame (see <see cref="BrowserFrame.IsScrollShiftable"/>),
/// and the DOM follows whenever the engine is free, which then paints the exact frame.
/// </summary>
partial class BrowserEngine
{
    private static readonly TimeSpan ScrollTickInterval = TimeSpan.FromMilliseconds(15);
    // the time constant of easing towards the target, in milliseconds
    private const double ScrollEasing = 60;

    private readonly object _scrollLock = new();
    private Timer? _scrollTimer;
    private TaskCompletionSource? _scrollSettled;
    private double _scrollPosition;
    private double _scrollTarget;
    private long _lastScrollTick;
    private int _scrollVersion;
    private int _appliedScrollVersion;
    private bool _isScrollSyncPending;

    /// <summary>
    /// Raised when <see cref="ScrollPosition"/> or <see cref="ScrollHeight"/> changed.
    /// </summary>
    public event EventHandler? ScrollChanged;

    /// <summary>
    /// Gets the vertical scroll position of the page as it should be shown now. While scrolling
    /// smoothly, it runs ahead of the DOM's <see cref="ScrollTop"/>.
    /// </summary>
    public double ScrollPosition
    {
        get
        {
            lock (_scrollLock)
            {
                return _scrollPosition;
            }
        }
    }

    private bool IsScrolling
    {
        get
        {
            lock (_scrollLock)
            {
                return _scrollTimer is not null || _isScrollSyncPending;
            }
        }
    }

    /// <summary>
    /// Gets the height of the page's content as of the last layout, at least the viewport's.
    /// </summary>
    public double ScrollHeight => Volatile.Read(ref _maxScrollTop) + ViewportHeight;

    /// <summary>
    /// Scrolls the page to the given vertical position.
    /// </summary>
    /// <param name="top">The position in CSS pixels; clamped to the page.</param>
    /// <param name="animate">If the page should get there smoothly.</param>
    /// <returns>The task completing once the DOM has arrived at the position.</returns>
    public Task ScrollToAsync(double top, bool animate = true) =>
        RequestScroll(_ => top, animate);

    private Task RequestScroll(Func<double, double> getTarget, bool animate)
    {
        TaskCompletionSource settled;
        var isJump = false;

        lock (_scrollLock)
        {
            _scrollTarget = Math.Round(Math.Clamp(getTarget(_scrollTarget), 0, Volatile.Read(ref _maxScrollTop)));
            _scrollVersion++;
            _scrollSettled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settled = _scrollSettled;

            if (!animate && _scrollPosition != _scrollTarget)
            {
                _scrollPosition = _scrollTarget;
                isJump = true;
            }

            if (_scrollTimer is null && !_isDisposed)
            {
                _lastScrollTick = Environment.TickCount64;
                _scrollTimer = new Timer(OnScrollTick, null, TimeSpan.Zero, ScrollTickInterval);
            }
        }

        if (isJump)
        {
            OnScrollChanged();
        }

        return settled.Task;
    }

    private void OnScrollTick(object? state)
    {
        bool isChanged;
        var isEnded = false;

        lock (_scrollLock)
        {
            var now = Environment.TickCount64;
            var distance = _scrollTarget - _scrollPosition;
            var step = distance * (1 - Math.Exp(-(now - _lastScrollTick) / ScrollEasing));
            _lastScrollTick = now;

            // whole pixels keep the text crisp, at least one of them keeps it moving
            var rounded = Math.Round(step);
            step = Math.Abs(rounded) >= 1 ? rounded : Math.Sign(distance) * Math.Min(1, Math.Abs(distance));
            isChanged = step != 0;
            _scrollPosition += step;

            if (_scrollPosition == _scrollTarget || _isDisposed)
            {
                _scrollTimer?.Dispose();
                _scrollTimer = null;
                isEnded = true;
            }
        }

        if (isChanged)
        {
            OnScrollChanged();
        }

        SyncScroll();

        // the hover state was kept while scrolling
        if (isEnded && _hasPointer)
        {
            MovePointer(_lastPointer.X, _lastPointer.Y);
        }
    }

    /// <summary>
    /// Brings the DOM to the current scroll position. At most one update waits for the
    /// engine; it takes the position the scrolling has arrived at by then.
    /// </summary>
    private void SyncScroll()
    {
        lock (_scrollLock)
        {
            if (_isScrollSyncPending || _isDisposed)
            {
                return;
            }

            _isScrollSyncPending = true;
        }

        RunExclusiveAsync(() =>
        {
            double position;
            int version;

            lock (_scrollLock)
            {
                position = _scrollPosition;
                version = _scrollVersion;
                _isScrollSyncPending = false;
            }

            if (_document?.DocumentElement is IElement root && ScrollTop != position)
            {
                _isSyncingScroll = true;

                try
                {
                    _harness.SetScrollTop(root, position, Math.Max(position, _maxScrollTop));
                }
                finally
                {
                    _isSyncingScroll = false;
                }

                Fire(_document, new Event("scroll", false, false));
            }

            TaskCompletionSource? settled = null;

            lock (_scrollLock)
            {
                _appliedScrollVersion = version;

                if (_scrollTimer is null && version == _scrollVersion && position == _scrollPosition)
                {
                    settled = _scrollSettled;
                    _scrollSettled = null;
                }
            }

            settled?.TrySetResult();
        }).ContinueWith(task =>
        {
            lock (_scrollLock)
            {
                _isScrollSyncPending = false;
            }

            if (!_isDisposed)
            {
                OnError(task.Exception!.GetBaseException());
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Takes over a scroll position the page arrived at by itself (scripts, fragments), unless
    /// the user is scrolling. Requires exclusive access.
    /// </summary>
    private void AdoptScrollPosition(double top)
    {
        var isChanged = false;

        lock (_scrollLock)
        {
            if (_scrollTimer is null && !_isScrollSyncPending && _appliedScrollVersion == _scrollVersion && _scrollPosition != top)
            {
                _scrollPosition = _scrollTarget = top;
                isChanged = true;
            }
        }

        if (isChanged)
        {
            OnScrollChanged();
        }
    }

    private void ResetScrollPosition()
    {
        lock (_scrollLock)
        {
            _scrollTimer?.Dispose();
            _scrollTimer = null;
            _scrollPosition = _scrollTarget = 0;
            _appliedScrollVersion = _scrollVersion;
            _scrollSettled?.TrySetResult();
            _scrollSettled = null;
        }

        OnScrollChanged();
    }

    private void UpdateScrollExtent(double contentHeight, int layoutVersion)
    {
        var max = Math.Max(0, contentHeight - ViewportHeight);
        _scrollExtentVersion = layoutVersion;

        if (Volatile.Read(ref _maxScrollTop) != max)
        {
            Volatile.Write(ref _maxScrollTop, max);
            OnScrollChanged();
        }
    }

    private void OnScrollChanged()
    {
        ScrollChanged?.Invoke(this, EventArgs.Empty);

        // the cursor follows what scrolls below it right away
        if (_hasPointer && FindPointerTarget(_lastPointer.X, _lastPointer.Y) is { } target)
        {
            SetPointerState(target.Href, target.Cursor);
        }
    }
}
