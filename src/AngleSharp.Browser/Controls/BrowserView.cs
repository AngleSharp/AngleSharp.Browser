namespace AngleSharp.Browser.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

/// <summary>
/// Displays the frames painted by a <see cref="BrowserEngine"/> and
/// forwards pointer and keyboard input to it. While the page scrolls, the
/// last frame (painted with some overscan) is shifted until the next one
/// arrives; an overlay scrollbar shows and changes the scroll position.
/// </summary>
public sealed class BrowserView : Control
{
    public static readonly StyledProperty<BrowserEngine?> EngineProperty =
        AvaloniaProperty.Register<BrowserView, BrowserEngine?>(nameof(Engine));

    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly double WheelScrollDistance = 48;
    private const double ScrollbarWidth = 12;
    private const double MinThumbHeight = 24;

    private static readonly IBrush ScrollbarTrackBrush = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0));
    private static readonly IBrush ScrollbarThumbBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x50, 0x50, 0x50));
    private static readonly IBrush ScrollbarActiveThumbBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x40, 0x40, 0x40));

    private static readonly Dictionary<Key, string> SpecialKeys = new()
    {
        [Key.Enter] = "Enter",
        [Key.Back] = "Backspace",
        [Key.Tab] = "Tab",
        [Key.Escape] = "Escape",
        [Key.Delete] = "Delete",
        [Key.Space] = " ",
        [Key.Left] = "ArrowLeft",
        [Key.Right] = "ArrowRight",
        [Key.Up] = "ArrowUp",
        [Key.Down] = "ArrowDown",
        [Key.PageUp] = "PageUp",
        [Key.PageDown] = "PageDown",
        [Key.Home] = "Home",
        [Key.End] = "End",
    };

    private readonly Cursor _arrowCursor = new(StandardCursorType.Arrow);
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);
    private readonly Cursor _textCursor = new(StandardCursorType.Ibeam);
    private BrowserEngine? _engine;
    private Bitmap? _frame;
    private FrameInfo _frameInfo;
    private int _isDirty;
    private int _isPaintLoopRunning;
    private int _isScrollRenderPending;
    private bool _isOverScrollbar;
    private (double PointerY, double ScrollTop)? _thumbDrag;

    /// <summary>
    /// Where a displayed frame was painted, to shift it to the current scroll position.
    /// </summary>
    private readonly record struct FrameInfo(double ScrollTop, int OverscanTop, bool IsScrollShiftable);

    static BrowserView()
    {
        FocusableProperty.OverrideDefaultValue<BrowserView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<BrowserView>(true);
    }

    /// <summary>
    /// Gets or sets the engine to display.
    /// </summary>
    public BrowserEngine? Engine
    {
        get => GetValue(EngineProperty);
        set => SetValue(EngineProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

        if (_frame is not null)
        {
            // until the next frame arrives, the page scrolls by moving this one
            var shift = _frameInfo.IsScrollShiftable && _engine is not null ? _frameInfo.ScrollTop - _engine.ScrollPosition : 0;
            context.DrawImage(_frame, new Rect(new Point(0, shift - _frameInfo.OverscanTop), _frame.Size));
        }

        if (TryGetScrollbar(out var track, out var thumb))
        {
            var isActive = _isOverScrollbar || _thumbDrag is not null;

            if (isActive)
            {
                context.FillRectangle(ScrollbarTrackBrush, track);
            }

            var width = isActive ? ScrollbarWidth - 4 : ScrollbarWidth - 6;
            var bar = new Rect(track.Right - 2 - width, thumb.Y + 2, width, Math.Max(0, thumb.Height - 4));
            context.DrawRectangle(isActive ? ScrollbarActiveThumbBrush : ScrollbarThumbBrush, null, bar, width / 2, width / 2);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EngineProperty)
        {
            if (_engine is not null)
            {
                _engine.Invalidated -= OnEngineInvalidated;
                _engine.HoverChanged -= OnEngineHoverChanged;
                _engine.ScrollChanged -= OnEngineScrollChanged;
            }

            _engine = change.GetNewValue<BrowserEngine?>();

            if (_engine is not null)
            {
                _engine.Invalidated += OnEngineInvalidated;
                _engine.HoverChanged += OnEngineHoverChanged;
                _engine.ScrollChanged += OnEngineScrollChanged;
                ResizeEngine(Bounds.Size);
                RequestPaint();
            }
        }
        else if (change.Property == BoundsProperty)
        {
            ResizeEngine(change.GetNewValue<Rect>().Size);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frame?.Dispose();
        _frame = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);

        if (_thumbDrag is { } drag && TryGetScrollbar(out var track, out var thumb) && _engine is BrowserEngine engine)
        {
            // the thumb moves along the track, which stands for the scrollable distance
            var max = engine.ScrollHeight - engine.ViewportHeight;
            var travel = Math.Max(1, track.Height - thumb.Height);
            Observe(engine.ScrollToAsync(drag.ScrollTop + (position.Y - drag.PointerY) * max / travel, animate: false));
        }
        else
        {
            SetOverScrollbar(IsOnScrollbar(position));

            if (!_isOverScrollbar)
            {
                _engine?.MovePointer(position.X, position.Y);
            }
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetOverScrollbar(false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && IsOnScrollbar(point.Position))
        {
            PressScrollbar(e, point.Position);
            return;
        }

        switch (point.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                Focus();
                Observe(_engine?.PointerDownAsync(point.Position.X, point.Position.Y, GetModifiers(e.KeyModifiers)));
                e.Handled = true;
                break;
            case PointerUpdateKind.XButton1Pressed:
                Observe(_engine?.GoBackAsync());
                e.Handled = true;
                break;
            case PointerUpdateKind.XButton2Pressed:
                Observe(_engine?.GoForwardAsync());
                e.Handled = true;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_thumbDrag is not null)
        {
            _thumbDrag = null;
            e.Pointer.Capture(null);
            SetOverScrollbar(IsOnScrollbar(e.GetPosition(this)));
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            var position = e.GetPosition(this);
            Observe(_engine?.PointerUpAsync(position.X, position.Y, GetModifiers(e.KeyModifiers)));
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Observe(_engine?.ScrollAsync(-e.Delta.X * WheelScrollDistance, -e.Delta.Y * WheelScrollDistance));
        e.Handled = true;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var engine = _engine;

        if (engine is null)
        {
            return;
        }

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await PasteAsync(engine);
        }
        else if (SpecialKeys.TryGetValue(e.Key, out var key) || !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // shortcuts with control or alt are left to the window; keys producing
            // characters stay unhandled, as Avalonia only raises TextInput for those
            e.Handled = key is not null && key != " ";
            Observe(engine.KeyDownAsync(key ?? e.KeySymbol ?? e.Key.ToString(), GetModifiers(e.KeyModifiers)));
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (_engine is not null)
        {
            var key = SpecialKeys.TryGetValue(e.Key, out var special) ? special : e.KeySymbol ?? e.Key.ToString();
            Observe(_engine.KeyUpAsync(key, GetModifiers(e.KeyModifiers)));
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (_engine is not null && !string.IsNullOrEmpty(e.Text))
        {
            Observe(_engine.TextInputAsync(e.Text));
            e.Handled = true;
        }
    }

    private async Task PasteAsync(BrowserEngine engine)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync();

        if (!string.IsNullOrEmpty(text))
        {
            Observe(engine.TextInputAsync(text));
        }
    }

    private void PressScrollbar(PointerPressedEventArgs e, Point position)
    {
        e.Handled = true;

        if (_engine is not BrowserEngine engine || !TryGetScrollbar(out _, out var thumb))
        {
            return;
        }

        if (position.Y >= thumb.Top && position.Y <= thumb.Bottom)
        {
            _thumbDrag = (position.Y, engine.ScrollPosition);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
        {
            // like a page up / down towards the pressed position
            var page = engine.ViewportHeight * 0.875;
            Observe(engine.ScrollToAsync(engine.ScrollPosition + (position.Y < thumb.Top ? -page : page)));
        }
    }

    /// <summary>
    /// Gets the scrollbar's track and thumb, if the page is taller than the viewport.
    /// </summary>
    private bool TryGetScrollbar(out Rect track, out Rect thumb)
    {
        track = thumb = default;
        var engine = _engine;

        if (engine is null || _frame is null)
        {
            return false;
        }

        var viewport = (double)engine.ViewportHeight;
        var content = engine.ScrollHeight;

        if (content <= viewport + 1 || Bounds.Width < 2 * ScrollbarWidth)
        {
            return false;
        }

        track = new Rect(Bounds.Width - ScrollbarWidth, 0, ScrollbarWidth, Bounds.Height);
        var height = Math.Min(track.Height, Math.Max(MinThumbHeight, track.Height * viewport / content));
        var progress = Math.Clamp(engine.ScrollPosition / (content - viewport), 0, 1);
        thumb = new Rect(track.X, (track.Height - height) * progress, ScrollbarWidth, height);
        return true;
    }

    private bool IsOnScrollbar(Point position) =>
        TryGetScrollbar(out var track, out _) && track.Contains(position);

    private void SetOverScrollbar(bool isOverScrollbar)
    {
        if (_isOverScrollbar != isOverScrollbar)
        {
            _isOverScrollbar = isOverScrollbar;
            UpdateCursor();
            InvalidateVisual();
        }
    }

    private void UpdateCursor()
    {
        Cursor = _isOverScrollbar ? _arrowCursor : _engine?.Cursor switch
        {
            BrowserCursor.Pointer => _handCursor,
            BrowserCursor.Text => _textCursor,
            _ => _arrowCursor,
        };
    }

    private void ResizeEngine(Size size)
    {
        if (_engine is not null && size.Width >= 1 && size.Height >= 1)
        {
            _engine.Resize((int)size.Width, (int)size.Height);
        }
    }

    private void OnEngineInvalidated(object? sender, EventArgs e) => RequestPaint();

    private void OnEngineHoverChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateCursor);

    private void OnEngineScrollChanged(object? sender, EventArgs e)
    {
        // scrolling ticks faster than the UI may render, so they are coalesced
        if (Interlocked.Exchange(ref _isScrollRenderPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _isScrollRenderPending, 0);
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Schedules painting a new frame. Requests arriving while a frame is
    /// painted are coalesced into a single follow-up frame.
    /// </summary>
    private void RequestPaint()
    {
        Interlocked.Exchange(ref _isDirty, 1);

        if (Interlocked.CompareExchange(ref _isPaintLoopRunning, 1, 0) == 0)
        {
            _ = PaintLoopAsync();
        }
    }

    /// <summary>
    /// Paints frames while there are changes. The engine lays out the next frame while the
    /// previous one is rasterized and presented on another thread.
    /// </summary>
    private async Task PaintLoopAsync()
    {
        Task? presenting = null;

        try
        {
            while (Interlocked.Exchange(ref _isDirty, 0) == 1 && _engine is BrowserEngine engine)
            {
                var watch = Stopwatch.StartNew();
                var frame = await engine.PrepareFrameAsync().ConfigureAwait(false);

                if (presenting is not null)
                {
                    await presenting.ConfigureAwait(false);
                }

                presenting = frame is null ? null : Task.Run(() => PresentAsync(frame));
                var remaining = MinFrameInterval - watch.Elapsed;

                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
            }

            if (presenting is not null)
            {
                await presenting.ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // the engine has been shut down
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Painting failed: {ex}");
        }
        finally
        {
            Volatile.Write(ref _isPaintLoopRunning, 0);

            if (Volatile.Read(ref _isDirty) == 1)
            {
                RequestPaint();
            }
        }
    }

    private async Task PresentAsync(BrowserFrame frame)
    {
        var pixels = frame.Rasterize();
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            for (var row = 0; row < pixels.Height; row++)
            {
                Marshal.Copy(pixels.Data, row * pixels.RowBytes, buffer.Address + row * buffer.RowBytes, pixels.RowBytes);
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var previous = _frame;
            _frame = bitmap;
            _frameInfo = new FrameInfo(frame.ScrollTop, frame.OverscanTop, frame.IsScrollShiftable);
            previous?.Dispose();
            InvalidateVisual();
        });
    }

    private static InputModifiers GetModifiers(KeyModifiers modifiers)
    {
        var result = InputModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= InputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= InputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= InputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= InputModifiers.Meta;

        return result;
    }

    private static async void Observe(Task? task)
    {
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Browser operation failed: {ex}");
        }
    }
}
