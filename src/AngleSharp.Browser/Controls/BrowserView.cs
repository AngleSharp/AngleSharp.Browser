namespace AngleSharp.Browser.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

/// <summary>
/// Displays the frames painted by a <see cref="BrowserEngine"/> and
/// forwards pointer and keyboard input to it.
/// </summary>
public sealed class BrowserView : Control
{
    public static readonly StyledProperty<BrowserEngine?> EngineProperty =
        AvaloniaProperty.Register<BrowserView, BrowserEngine?>(nameof(Engine));

    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly double WheelScrollDistance = 48;

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
    private int _isDirty;
    private int _isPaintLoopRunning;

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
            context.DrawImage(_frame, new Rect(_frame.Size));
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
            }

            _engine = change.GetNewValue<BrowserEngine?>();

            if (_engine is not null)
            {
                _engine.Invalidated += OnEngineInvalidated;
                _engine.HoverChanged += OnEngineHoverChanged;
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
        _engine?.MovePointer(position.X, position.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

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

        if (e.InitialPressMouseButton == MouseButton.Left)
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
            // shortcuts with control or alt are left to the window
            e.Handled = true;
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

    private void ResizeEngine(Size size)
    {
        if (_engine is not null && size.Width >= 1 && size.Height >= 1)
        {
            _engine.Resize((int)size.Width, (int)size.Height);
        }
    }

    private void OnEngineInvalidated(object? sender, EventArgs e) => RequestPaint();

    private void OnEngineHoverChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Cursor = _engine?.Cursor switch
            {
                BrowserCursor.Pointer => _handCursor,
                BrowserCursor.Text => _textCursor,
                _ => _arrowCursor,
            };
        });
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

    private async Task PaintLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _isDirty, 0) == 1 && _engine is BrowserEngine engine)
            {
                var watch = Stopwatch.StartNew();
                var image = await engine.PaintAsync().ConfigureAwait(false);

                if (image is not null)
                {
                    using var stream = new MemoryStream(image.Data);
                    var bitmap = new Bitmap(stream);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var previous = _frame;
                        _frame = bitmap;
                        previous?.Dispose();
                        InvalidateVisual();
                    });
                }

                var remaining = MinFrameInterval - watch.Elapsed;

                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
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
