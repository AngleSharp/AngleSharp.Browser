namespace AngleSharp.Browser;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Io;
using AngleSharp.Js;

/// <summary>
/// The cursor a host should display for the current pointer position.
/// </summary>
public enum BrowserCursor
{
    /// <summary>
    /// The default arrow.
    /// </summary>
    Default,
    /// <summary>
    /// A hand, e.g., for links.
    /// </summary>
    Pointer,
    /// <summary>
    /// An I-beam, e.g., for text fields.
    /// </summary>
    Text,
}

/// <summary>
/// The modifier keys held during an input event.
/// </summary>
[Flags]
public enum InputModifiers
{
    /// <summary>
    /// No modifier.
    /// </summary>
    None = 0,
    /// <summary>
    /// The shift key.
    /// </summary>
    Shift = 1,
    /// <summary>
    /// The control key.
    /// </summary>
    Control = 2,
    /// <summary>
    /// The alt key.
    /// </summary>
    Alt = 4,
    /// <summary>
    /// The meta (Windows / Command) key.
    /// </summary>
    Meta = 8,
}

partial class BrowserEngine
{
    private const double LineScrollDistance = 40;

    private readonly object _pointerLock = new();
    private (double X, double Y)? _pendingPointer;
    private (double X, double Y) _lastPointer;
    private bool _hasPointer;
    private bool _isPointerWorkerRunning;
    private int _hitTestVersion = -1;
    private IElement? _hovered;
    private IElement? _pressed;
    private string? _hoveredLink;
    private BrowserCursor _cursor;

    /// <summary>
    /// Raised when the hovered link or the cursor changed.
    /// </summary>
    public event EventHandler? HoverChanged;

    /// <summary>
    /// Gets the address of the link below the pointer, if any.
    /// </summary>
    public string? HoveredLink => _hoveredLink;

    /// <summary>
    /// Gets the cursor to display for the current pointer position.
    /// </summary>
    public BrowserCursor Cursor => _cursor;

    /// <summary>
    /// Moves the pointer to the given viewport position. The cursor and the hovered
    /// link are looked up in the last frame right away; the DOM events are coalesced,
    /// i.e., only the latest position is processed once the engine is free.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    public void MovePointer(double x, double y)
    {
        _lastPointer = (x, y);
        _hasPointer = true;

        if (FindPointerTarget(x, y) is { } target)
        {
            SetPointerState(target.Href, target.Cursor);
        }

        lock (_pointerLock)
        {
            _pendingPointer = (x, y);

            if (_isPointerWorkerRunning)
            {
                return;
            }

            _isPointerWorkerRunning = true;
        }

        _ = ProcessPointerMovesAsync();
    }

    /// <summary>
    /// Moves the pointer to the given viewport position and waits until the
    /// hover state has been updated.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task MovePointerAsync(double x, double y, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() => UpdateHover(x, y), cancellationToken);

    /// <summary>
    /// Presses the primary pointer button at the given viewport position.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="modifiers">The held modifier keys.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task PointerDownAsync(double x, double y, InputModifiers modifiers = InputModifiers.None, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            var target = HitTest(x, y);
            _pressed = target;

            if (target is not null)
            {
                KeepAnimating(InteractionDuration);

                if (Fire(target, CreateMouseEvent("mousedown", x, y, modifiers)))
                {
                    UpdateFocus(target);
                }
            }
        }, cancellationToken);

    /// <summary>
    /// Releases the primary pointer button at the given viewport position.
    /// If it was pressed on the same element, the element is clicked, which
    /// may navigate (the returned task then completes once loaded).
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="modifiers">The held modifier keys.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public async Task PointerUpAsync(double x, double y, InputModifiers modifiers = InputModifiers.None, CancellationToken cancellationToken = default)
    {
        var navigation = await RunExclusiveAsync(() =>
        {
            var target = HitTest(x, y);
            var pressed = _pressed;
            _pressed = null;

            if (target is null)
            {
                return null;
            }

            KeepAnimating(InteractionDuration);
            Fire(target, CreateMouseEvent("mouseup", x, y, modifiers));
            var clickTarget = pressed is null ? null : GetCommonAncestor(pressed, target);
            return clickTarget is null ? null : Click(clickTarget, x, y, modifiers);
        }, cancellationToken).ConfigureAwait(false);

        if (navigation is not null)
        {
            await navigation.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clicks at the given viewport position, i.e., presses and releases the
    /// primary pointer button.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="modifiers">The held modifier keys.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public async Task ClickAsync(double x, double y, InputModifiers modifiers = InputModifiers.None, CancellationToken cancellationToken = default)
    {
        await PointerDownAsync(x, y, modifiers, cancellationToken).ConfigureAwait(false);
        await PointerUpAsync(x, y, modifiers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scrolls the element below the pointer (or the page) by the given delta.
    /// </summary>
    /// <param name="deltaX">The horizontal distance in CSS pixels.</param>
    /// <param name="deltaY">The vertical distance in CSS pixels.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    /// <remarks>
    /// The page scrolls smoothly; the returned task completes once it arrived. Unless the
    /// pointer is above a scroll container, it starts right away, without waiting for the engine.
    /// </remarks>
    public async Task ScrollAsync(double deltaX, double deltaY, CancellationToken cancellationToken = default)
    {
        if (_hasPointer && FindPointerTarget(_lastPointer.X, _lastPointer.Y) is { IsInScrollContainer: false })
        {
            await RequestScroll(top => top + deltaY, animate: true).ConfigureAwait(false);
            return;
        }

        var scrolling = await RunExclusiveAsync(() =>
        {
            var container = FindScrollContainer(_hovered?.Owner == _document ? _hovered : null, deltaY);

            if (container is null)
            {
                return ScrollPage(deltaY);
            }

            container.ScrollBy(deltaX, deltaY);
            Fire(container, new Event("scroll", false, false));
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        await scrolling.ConfigureAwait(false);

        // the content below the pointer changed
        MovePointer(_lastPointer.X, _lastPointer.Y);
    }

    /// <summary>
    /// Presses a key. The key is given by its DOM name, e.g., "a", "Enter",
    /// "Backspace", "Tab" or "ArrowDown". Text is entered via
    /// <see cref="TextInputAsync"/>.
    /// </summary>
    /// <param name="key">The DOM key value.</param>
    /// <param name="modifiers">The held modifier keys.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public async Task KeyDownAsync(string key, InputModifiers modifiers = InputModifiers.None, CancellationToken cancellationToken = default)
    {
        var navigation = await RunExclusiveAsync(() => HandleKeyDown(key, modifiers), cancellationToken).ConfigureAwait(false);

        if (navigation is not null)
        {
            await navigation.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases a key.
    /// </summary>
    /// <param name="key">The DOM key value.</param>
    /// <param name="modifiers">The held modifier keys.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task KeyUpAsync(string key, InputModifiers modifiers = InputModifiers.None, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            if (GetKeyboardTarget() is IElement target)
            {
                Fire(target, CreateKeyboardEvent("keyup", key, modifiers));
            }
        }, cancellationToken);

    /// <summary>
    /// Enters text into the focused text field, if any.
    /// </summary>
    /// <param name="text">The text to insert.</param>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task TextInputAsync(string text, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            var focused = _document?.ActiveElement;

            if (FormControls.IsTextEntry(focused))
            {
                InsertText(focused!, text);
            }
        }, cancellationToken);

    private async Task ProcessPointerMovesAsync()
    {
        while (true)
        {
            (double X, double Y) position;

            lock (_pointerLock)
            {
                if (_pendingPointer is null || _isDisposed)
                {
                    _isPointerWorkerRunning = false;
                    return;
                }

                position = _pendingPointer.Value;
                _pendingPointer = null;
            }

            try
            {
                await RunExclusiveAsync(() => UpdateHover(position.X, position.Y)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }
    }

    /// <summary>
    /// Updates the hover state for the pointer at the given position. The last frame
    /// answers which element is there, even if the layout changed since: the next frame
    /// updates the hover state again. Requires exclusive access.
    /// </summary>
    private void UpdateHover(double x, double y, bool isMove = true)
    {
        if (!isMove && !_hasPointer)
        {
            return;
        }

        var target = FindPointerTarget(x, y);
        var element = target is { } found ? found.Element : HitTest(x, y);
        var previous = _hovered;
        _lastPointer = (x, y);
        _hasPointer = true;

        if (element != previous)
        {
            if (previous is not null && previous.Owner == _document)
            {
                Fire(previous, CreateMouseEvent("mouseout", x, y, InputModifiers.None));
            }

            if (element is not null)
            {
                Fire(element, CreateMouseEvent("mouseover", x, y, InputModifiers.None));
            }

            _hovered = element;
            KeepAnimating(InteractionDuration);
        }

        if (element is not null && isMove)
        {
            Fire(element, CreateMouseEvent("mousemove", x, y, InputModifiers.None));
        }

        _harness.SetHoveredElement(element);

        if (target is { } info)
        {
            SetPointerState(info.Href, info.Cursor);
        }
        else
        {
            var link = FindLink(element);
            SetPointerState(link is IUrlUtilities utilities ? utilities.Href : null, GetCursor(element, link));
        }
    }

    private void SetPointerState(string? href, BrowserCursor cursor)
    {
        lock (_pointerLock)
        {
            if (href == _hoveredLink && cursor == _cursor)
            {
                return;
            }

            _hoveredLink = href;
            _cursor = cursor;
        }

        HoverChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Finds the top-most element at the given viewport position: in the last frame, if
    /// it shows the current layout, else by laying out the page.
    /// </summary>
    private IElement? HitTest(double x, double y)
    {
        _lastPointer = (x, y);
        _hasPointer = true;

        if (IsPointerFrameCurrent() && FindPointerTarget(x, y) is { } target)
        {
            return target.Element;
        }

        if (_document?.DocumentElement is null || !IsReadyForLayout())
        {
            return null;
        }

        // the renderer hit-tests against the unscrolled layout, i.e., page coordinates
        var position = (X: x, Y: y + ScrollTop);
        var version = Volatile.Read(ref _layoutVersion);
        var isStale = version != _hitTestVersion || _harness.HoveredElement?.Owner != _document;

        if (isStale && _harness.MousePosition == position)
        {
            // the harness only hit-tests again when the position changes
            position.Y += 0.001;
        }

        _hitTestVersion = version;
        _harness.MousePosition = position;
        var element = _harness.HoveredElement;
        return element?.Owner == _document ? element : null;
    }

    private Task? Click(IElement target, double x, double y, InputModifiers modifiers)
    {
        var activation = FindActivationTarget(target);
        var toggle = FormControls.IsToggle(activation) ? (IHtmlInputElement)activation! : null;
        var wasChecked = toggle?.IsChecked ?? false;

        if (toggle is not null)
        {
            var isRadio = toggle.Type.Equals("radio", StringComparison.OrdinalIgnoreCase);
            FormControls.SetChecked(toggle, isRadio || !wasChecked);
        }

        if (!Fire(target, CreateMouseEvent("click", x, y, modifiers)))
        {
            if (toggle is not null)
            {
                FormControls.SetChecked(toggle, wasChecked);
            }

            return null;
        }

        return Activate(activation, x, y, modifiers);
    }

    /// <summary>
    /// Runs the activation behavior of the given element, e.g., following a
    /// link or submitting a form. Returns the started navigation, if any.
    /// </summary>
    private Task? Activate(IElement? element, double x, double y, InputModifiers modifiers)
    {
        switch (element)
        {
            case IHtmlInputElement input when FormControls.IsToggle(input):
                FireChange(input);
                return null;
            case IHtmlInputElement input when IsType(input.Type, "submit") || IsType(input.Type, "image"):
                return Submit(input.Form, input);
            case IHtmlInputElement input when IsType(input.Type, "reset"):
                input.Form?.Reset();
                return null;
            case IHtmlButtonElement button when IsType(button.Type, "submit"):
                return Submit(button.Form, button);
            case IHtmlButtonElement button when IsType(button.Type, "reset"):
                button.Form?.Reset();
                return null;
            case IHtmlAnchorElement anchor:
                return FollowHyperlink(anchor, anchor.Href, modifiers);
            case IHtmlAreaElement area:
                return FollowHyperlink(area, area.Href, modifiers);
            case IHtmlLabelElement label when label.Control is IElement control:
                return Click(control, x, y, modifiers);
            case IHtmlElement { LocalName: "summary", ParentElement: IHtmlDetailsElement details }:
                details.IsOpen = !details.IsOpen;
                return null;
            default:
                return null;
        }
    }

    private Task? Submit(IHtmlFormElement? form, IHtmlElement submitter)
    {
        if (form is null)
        {
            return null;
        }

        return TrackNavigationAsync(() => form.SubmitAsync(submitter), form.Action);
    }

    private Task? FollowHyperlink<TElement>(TElement element, string href, InputModifiers modifiers)
        where TElement : class, IUrlUtilities, IElement
    {
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            RunScript(Uri.UnescapeDataString(href["javascript:".Length..]));
            return null;
        }

        var target = new Url(href);
        var current = new Url(_document!.Url);

        if (target.Fragment is not null && target.Href.Split('#')[0] == current.Href.Split('#')[0])
        {
            ScrollToFragment(target.Fragment);
            return null;
        }

        var targetName = element.GetAttribute("target");
        var isNewWindow = modifiers.HasFlag(InputModifiers.Control) || modifiers.HasFlag(InputModifiers.Meta) ||
            (!string.IsNullOrEmpty(targetName) && targetName is not ("_self" or "_parent" or "_top") && _context.FindChildFor(targetName) is null);

        if (isNewWindow)
        {
            var request = new WindowRequestedEventArgs(target.Href);
            WindowRequested?.Invoke(this, request);

            if (request.Handled)
            {
                return null;
            }

            // a new browsing context of this engine would never be shown, so the page navigates
            var navigation = DocumentRequest.Get(target, referer: _document.DocumentUri);
            return TrackNavigationAsync(() => _context.OpenAsync(navigation), href);
        }

        return TrackNavigationAsync(() => element.NavigateAsync(), href);
    }

    private void ScrollToFragment(string fragment)
    {
        var id = Uri.UnescapeDataString(fragment);
        var element = _document!.GetElementById(id) ?? _document.GetElementsByName(id).FirstOrDefault();

        if (element is not null)
        {
            element.ScrollIntoView();
        }
        else if (id.Length == 0 || id.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            ScrollPage(double.NegativeInfinity);
        }
    }

    private Task? HandleKeyDown(string key, InputModifiers modifiers)
    {
        if (GetKeyboardTarget() is not IElement target)
        {
            return null;
        }

        KeepAnimating(InteractionDuration);

        if (!Fire(target, CreateKeyboardEvent("keydown", key, modifiers)))
        {
            return null;
        }

        // the legacy keypress follows for keys producing a character, unless a shortcut is held
        if (LegacyKeyboardEvent.ProducesCharacter(key) && !modifiers.HasFlag(InputModifiers.Control) && !modifiers.HasFlag(InputModifiers.Meta) &&
            !Fire(target, CreateKeyboardEvent("keypress", key, modifiers)))
        {
            return null;
        }

        var focused = _document!.ActiveElement;
        var isTextEntry = FormControls.IsTextEntry(focused);

        switch (key)
        {
            case "Tab":
                MoveFocus(modifiers.HasFlag(InputModifiers.Shift) ? -1 : 1);
                return null;
            case "Enter" when focused is IHtmlTextAreaElement && isTextEntry:
                InsertText(focused, "\n");
                return null;
            case "Enter" when focused is IHtmlInputElement input && isTextEntry:
                return Submit(input.Form, input);
            case "Enter" when focused is IHtmlAnchorElement or IHtmlButtonElement:
            case " " when focused is IHtmlButtonElement || FormControls.IsToggle(focused):
                return Click(focused!, 0, 0, modifiers);
            case "Backspace" when isTextEntry:
                var value = FormControls.GetValue(focused!);

                if (value.Length > 0)
                {
                    FormControls.SetValue(focused!, value[..^1]);
                    Fire(focused!, new Event("input", true, false));
                }

                return null;
            case "ArrowUp" or "ArrowDown" when focused is IHtmlSelectElement select:
                if (FormControls.StepSelection(select, key == "ArrowUp" ? -1 : 1))
                {
                    FireChange(select);
                }

                return null;
        }

        if (!isTextEntry)
        {
            var page = ViewportHeight * 0.875;

            // the returned scrolling completes once the page arrived
            switch (key)
            {
                case "ArrowDown":
                    return ScrollPage(LineScrollDistance);
                case "ArrowUp":
                    return ScrollPage(-LineScrollDistance);
                case "PageDown":
                case " " when !modifiers.HasFlag(InputModifiers.Shift):
                    return ScrollPage(page);
                case "PageUp":
                case " ":
                    return ScrollPage(-page);
                case "Home":
                    return ScrollPage(double.NegativeInfinity);
                case "End":
                    return ScrollPage(double.PositiveInfinity);
            }
        }

        return null;
    }

    private IElement? GetKeyboardTarget()
    {
        var document = _document;
        return document?.ActiveElement ?? document?.Body ?? document?.DocumentElement;
    }

    private void InsertText(IElement element, string text)
    {
        if (element is IHtmlInputElement input)
        {
            text = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
            var room = input.MaxLength >= 0 ? input.MaxLength - input.Value.Length : int.MaxValue;
            text = text.Length > room ? text[..Math.Max(0, room)] : text;
        }

        if (text.Length > 0)
        {
            FormControls.SetValue(element, FormControls.GetValue(element) + text);
            Fire(element, new Event("input", true, false));
        }
    }

    private void UpdateFocus(IElement target)
    {
        var focusable = target.ParentElement is null ? null : (IElement?)target;

        while (focusable is not null && !FormControls.IsFocusable(focusable))
        {
            focusable = focusable is IHtmlLabelElement { Control: IElement control } ? control : focusable.ParentElement;
        }

        var current = _document?.ActiveElement;

        if (focusable != current)
        {
            if (focusable is IHtmlElement element)
            {
                element.DoFocus();
            }
            else
            {
                (current as IHtmlElement)?.DoBlur();
            }
        }
    }

    private void MoveFocus(int direction)
    {
        var candidates = _document!.All
            .Where(m => FormControls.IsFocusable(m) && !(int.TryParse(m.GetAttribute("tabindex"), out var index) && index < 0))
            .ToList();

        if (candidates.Count > 0)
        {
            var current = candidates.IndexOf(_document.ActiveElement!);
            var next = current < 0
                ? (direction > 0 ? 0 : candidates.Count - 1)
                : (current + direction + candidates.Count) % candidates.Count;
            (candidates[next] as IHtmlElement)?.DoFocus();
        }
    }

    /// <summary>
    /// Scrolls the page smoothly by the given distance. Requires exclusive access.
    /// </summary>
    /// <returns>The task completing once the page arrived.</returns>
    private Task ScrollPage(double delta)
    {
        if (_document?.DocumentElement is IElement root && IsReadyForLayout())
        {
            GetMaxScrollTop(root);
            AdoptScrollPosition(ScrollTop);
            return RequestScroll(top => top + delta, animate: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Determining the scroll extent requires a full layout, so it is only
    /// recomputed when the layout changed.
    /// </summary>
    private double GetMaxScrollTop(IElement root)
    {
        var version = Volatile.Read(ref _layoutVersion);

        if (version != _scrollExtentVersion)
        {
            UpdateScrollExtent(root.GetScrollHeight() - root.GetClientHeight() + ViewportHeight, version);
        }

        return _maxScrollTop;
    }

    private IElement? FindScrollContainer(IElement? element, double deltaY)
    {
        var document = _document;

        if (!IsReadyForLayout())
        {
            return null;
        }

        for (; element is not null && element != document?.Body && element != document?.DocumentElement; element = element.ParentElement)
        {
            var overflow = element.ComputeCurrentStyle().GetPropertyValue("overflow-y");

            if (overflow is "auto" or "scroll")
            {
                var max = element.GetScrollHeight() - element.GetClientHeight();
                var top = element.GetScrollTop();

                if ((deltaY < 0 && top > 0) || (deltaY > 0 && top < max))
                {
                    return element;
                }
            }
        }

        return null;
    }

    private void RunScript(string code)
    {
        try
        {
            _document?.ExecuteScript(code);
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    private bool Fire(IEventTarget target, Event ev)
    {
        try
        {
            // the return value of Dispatch is inverted in some AngleSharp versions
            target.Dispatch(ev);
            return !ev.IsDefaultPrevented;
        }
        catch (Exception ex)
        {
            OnError(ex);
            return true;
        }
    }

    private void FireChange(IElement element)
    {
        Fire(element, new Event("input", true, false));
        Fire(element, new Event("change", true, false));
    }

    private MouseEvent CreateMouseEvent(string type, double x, double y, InputModifiers modifiers) => new(
        type,
        bubbles: true,
        cancelable: true,
        view: _document?.DefaultView,
        detail: 1,
        screenX: (int)x,
        screenY: (int)y,
        clientX: (int)x,
        clientY: (int)y,
        ctrlKey: modifiers.HasFlag(InputModifiers.Control),
        altKey: modifiers.HasFlag(InputModifiers.Alt),
        shiftKey: modifiers.HasFlag(InputModifiers.Shift),
        metaKey: modifiers.HasFlag(InputModifiers.Meta));

    private KeyboardEvent CreateKeyboardEvent(string type, string key, InputModifiers modifiers) => new LegacyKeyboardEvent(
        type,
        _document?.DefaultView,
        key,
        string.Join(' ', Enum.GetValues<InputModifiers>().Where(m => m != InputModifiers.None && modifiers.HasFlag(m))));

    private static IElement? FindActivationTarget(IElement? element)
    {
        for (; element is not null; element = element.ParentElement)
        {
            switch (element)
            {
                case IHtmlAnchorElement or IHtmlAreaElement when element.HasAttribute("href"):
                case IHtmlButtonElement or IHtmlInputElement or IHtmlLabelElement or IHtmlElement { LocalName: "summary" }:
                    return element;
            }
        }

        return null;
    }

    private static IElement? FindLink(IElement? element)
    {
        for (; element is not null; element = element.ParentElement)
        {
            if (element is IHtmlAnchorElement or IHtmlAreaElement && element.HasAttribute("href"))
            {
                return element;
            }
        }

        return null;
    }

    private static BrowserCursor GetCursor(IElement? element, IElement? link)
    {
        var cursor = element?.ComputeCurrentStyle().GetPropertyValue("cursor");

        return cursor switch
        {
            "pointer" => BrowserCursor.Pointer,
            "text" => BrowserCursor.Text,
            _ when link is not null => BrowserCursor.Pointer,
            _ when FormControls.IsTextEntry(element) => BrowserCursor.Text,
            _ => BrowserCursor.Default,
        };
    }

    private static IElement? GetCommonAncestor(IElement a, IElement b)
    {
        for (var candidate = a; candidate is not null; candidate = candidate.ParentElement)
        {
            if (candidate == b || candidate.Contains(b))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsType(string type, string expected) =>
        type.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
