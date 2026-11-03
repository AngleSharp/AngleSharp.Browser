namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Browser;
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
    // where the selection of the focused list box was last changed from, and what is active in it
    private IElement? _listBoxAnchor;
    private IElement? _listBoxActive;
    // a list box whose selection changed when pressed, which it tells when released
    private IElement? _changedListBox;
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

        if (_selectPopup is { } popup && popup.View.Contains(x, y))
        {
            SetPointerState(null, BrowserCursor.Default);
        }
        else if (FindPointerTarget(x, y) is { } target)
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
            var popup = _selectPopup;

            // a press on the popup is for the popup alone, one elsewhere closes it
            if (popup is not null && popup.View.Contains(x, y))
            {
                return;
            }

            CloseSelectPopup();
            var hit = HitTest(x, y);
            var target = ResolveListBoxTarget(hit, x, y);
            _pressed = target;

            if (target is not null)
            {
                KeepAnimating(InteractionDuration);

                if (Fire(target, CreateMouseEvent(target, "mousedown", x, y, modifiers)))
                {
                    UpdateFocus(target);

                    if (hit is IHtmlSelectElement select && target != select)
                    {
                        PressListBoxRow(select, target, modifiers);
                    }
                    else if (IsDropdown(target) && popup?.Select != target)
                    {
                        // a press on the select that is open just closed it
                        ShowSelectPopup((IHtmlSelectElement)target);
                    }
                }
            }
            else if (hit is not null)
            {
                // something disabled takes no events, but a press on it still moves the focus
                UpdateFocus(hit);
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
            // a release on the popup chooses that row (also when pressed on the select, and
            // dragged there); a press on it that ends elsewhere leaves it open
            if (_selectPopup is { } popup && popup.View.Contains(x, y))
            {
                var row = popup.View.GetRowAt(x, y);
                _pressed = null;

                if (FormControls.IsSelectableOption(row))
                {
                    CommitSelectPopup(popup, row);
                }

                return null;
            }

            var target = ResolveListBoxTarget(HitTest(x, y), x, y);
            var pressed = _pressed;
            _pressed = null;
            var changedListBox = _changedListBox;
            _changedListBox = null;

            if (target is null)
            {
                if (changedListBox is not null)
                {
                    FireChange(changedListBox);
                }

                return null;
            }

            KeepAnimating(InteractionDuration);
            Fire(target, CreateMouseEvent(target, "mouseup", x, y, modifiers));

            if (changedListBox is not null)
            {
                FireChange(changedListBox);
            }

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
        // the wheel scrolls the popup below the pointer; the page scrolling closes it
        if (_selectPopup is not null)
        {
            var isOnPopup = await RunExclusiveAsync(() =>
            {
                if (_selectPopup is { } popup && _hasPointer && popup.View.Contains(_lastPointer.X, _lastPointer.Y))
                {
                    SetSelectPopup(popup.Select, popup.Highlighted, popup.View.ScrollTop + deltaY);
                    return true;
                }

                CloseSelectPopup();
                return false;
            }, cancellationToken).ConfigureAwait(false);

            if (isOnPopup)
            {
                return;
            }
        }

        if (_hasPointer && FindPointerTarget(_lastPointer.X, _lastPointer.Y) is { IsInScrollContainer: false })
        {
            await RequestScroll(top => top + deltaY, animate: true).ConfigureAwait(false);
            return;
        }

        var scrolling = await RunExclusiveAsync(() =>
        {
            var container = FindScrollContainer(IsInPage(_hovered?.Owner) ? _hovered : null, deltaY);

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
            var focused = FocusedDocument?.ActiveElement;

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

        // the popup is on top of the page: the row below the pointer gets highlighted
        if (_selectPopup is { } popup && popup.View.Contains(x, y))
        {
            _lastPointer = (x, y);
            _hasPointer = true;

            if (isMove && FormControls.IsSelectableOption(popup.View.GetRowAt(x, y)) && popup.View.GetRowAt(x, y) != popup.Highlighted)
            {
                SetSelectPopup(popup.Select, popup.View.GetRowAt(x, y), popup.View.ScrollTop);
            }

            return;
        }

        var target = FindPointerTarget(x, y);
        var element = target is { } found ? found.Element : HitTest(x, y);
        var previous = _hovered;
        _lastPointer = (x, y);
        _hasPointer = true;

        if (element != previous)
        {
            if (previous is not null && IsInPage(previous.Owner))
            {
                Fire(previous, CreateMouseEvent(previous, "mouseout", x, y, InputModifiers.None));
            }

            if (element is not null)
            {
                Fire(element, CreateMouseEvent(element, "mouseover", x, y, InputModifiers.None));
            }

            _hovered = element;
            KeepAnimating(InteractionDuration);
        }

        if (element is not null && isMove)
        {
            Fire(element, CreateMouseEvent(element, "mousemove", x, y, InputModifiers.None));
        }

        SetHoveredInDocuments(element);

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
        return element?.Owner == _document ? HitTestInFrame(element, x, y) : null;
    }

    /// <summary>
    /// What the pointer is at in a list box, whose rows have no boxes of their own: a row, or the
    /// select below the last one. Disabled ones don't take part in mouse events (null), as in browsers.
    /// </summary>
    private static IElement? ResolveListBoxTarget(IElement? hit, double x, double y)
    {
        if (hit is not IHtmlSelectElement select)
        {
            return hit;
        }

        if (select.IsDisabled)
        {
            return null;
        }

        if (!select.IsListBox())
        {
            return hit;
        }

        var row = select.GetListBoxRowAt(x, y);
        return row is IHtmlOptionElement && !FormControls.IsSelectableOption(row) ? null : row ?? select;
    }

    /// <summary>
    /// Changes the selection of a list box as a press on a row does: the row alone, with Ctrl (or
    /// Cmd) toggling it, with Shift the rows from the last one pressed without Shift.
    /// </summary>
    private void PressListBoxRow(IHtmlSelectElement select, IElement row, InputModifiers modifiers)
    {
        if (!FormControls.IsSelectableOption(row))
        {
            return;
        }

        var selected = new List<IElement>();

        if (select.IsMultiple && modifiers.HasFlag(InputModifiers.Shift))
        {
            selected.AddRange(FormControls.GetOptionRange(select, GetListBoxAnchor(select) ?? row, row));
        }
        else if (select.IsMultiple && (modifiers.HasFlag(InputModifiers.Control) || modifiers.HasFlag(InputModifiers.Meta)))
        {
            selected.AddRange(select.SelectedOptions);

            if (!selected.Remove(row))
            {
                selected.Add(row);
            }

            _listBoxAnchor = row;
        }
        else
        {
            selected.Add(row);
            _listBoxAnchor = row;
        }

        _listBoxActive = row;

        if (FormControls.SetSelectedOptions(select, selected))
        {
            _changedListBox = select;
        }

        select.ScrollListBoxRowIntoView(row);
    }

    /// <summary>
    /// Moves the selection of a list box to the next selectable option, with Shift extending it
    /// from its anchor.
    /// </summary>
    private bool StepListBox(IHtmlSelectElement select, int delta, InputModifiers modifiers)
    {
        var options = select.Options.OfType<IElement>().ToList();
        var active = GetListBoxActive(select);
        var index = active is null ? (delta > 0 ? -1 : options.Count) : options.IndexOf(active);

        do
        {
            index += delta;
        }
        while (index >= 0 && index < options.Count && !FormControls.IsSelectableOption(options[index]));

        if (index < 0 || index >= options.Count)
        {
            return false;
        }

        var next = options[index];
        var isExtending = select.IsMultiple && modifiers.HasFlag(InputModifiers.Shift);

        if (!isExtending)
        {
            _listBoxAnchor = next;
        }

        _listBoxActive = next;
        var hasChanged = FormControls.SetSelectedOptions(select, isExtending ? FormControls.GetOptionRange(select, GetListBoxAnchor(select) ?? next, next) : [next]);
        select.ScrollListBoxRowIntoView(next);
        return hasChanged;
    }

    private IElement? GetListBoxAnchor(IHtmlSelectElement select) =>
        IsOptionOf(select, _listBoxAnchor) ? _listBoxAnchor : GetListBoxActive(select);

    private IElement? GetListBoxActive(IHtmlSelectElement select) =>
        IsOptionOf(select, _listBoxActive) ? _listBoxActive : select.SelectedOptions.LastOrDefault();

    private static bool IsOptionOf(IHtmlSelectElement select, IElement? option) =>
        option is not null && select.Contains(option);

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

        if (!Fire(target, CreateMouseEvent(target, "click", x, y, modifiers)))
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
            case IHtmlLabelElement label when FormControls.GetLabeledControl(label) is { } control:
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

        if (form.Owner is { } owner && !ReferenceEquals(owner, _document))
        {
            return NavigateInFrameAsync(owner, () => form.SubmitAsync(submitter));
        }

        return TrackNavigationAsync(() => form.SubmitAsync(submitter), form.Action);
    }

    private Task? FollowHyperlink<TElement>(TElement element, string href, InputModifiers modifiers)
        where TElement : class, IUrlUtilities, IElement
    {
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            // in the document of the link: one in an iframe has no say over the page
            RunScript(element.Owner, Uri.UnescapeDataString(href["javascript:".Length..]));
            return null;
        }

        var target = new Url(href);
        var document = element.Owner ?? _document!;
        var current = new Url(document.Url);

        if (target.Fragment is not null && target.Href.Split('#')[0] == current.Href.Split('#')[0])
        {
            ScrollToFragment(document, target.Fragment);
            return null;
        }

        var targetName = element.GetAttribute("target");
        var security = document.Context?.Security ?? Sandboxes.None;

        // what a sandboxed frame isn't allowed to open or navigate
        if ((targetName is "_top" or "_parent" && !ReferenceEquals(document, _document) && security.HasFlag(Sandboxes.TopLevelNavigation)) ||
            (security.HasFlag(Sandboxes.AuxiliaryNavigation) && targetName is not (null or "" or "_self" or "_parent" or "_top")))
        {
            return null;
        }

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

        if (!ReferenceEquals(document, _document))
        {
            // the page itself for `_top`, the one around it for `_parent`, else the iframe
            var aimed = targetName switch
            {
                "_top" => _context,
                "_parent" => document.Context?.Parent ?? _context,
                _ => document.Context ?? _context,
            };

            if (ReferenceEquals(aimed, _context))
            {
                return TrackNavigationAsync(() => _context.OpenAsync(DocumentRequest.Get(target, referer: document.DocumentUri)), href);
            }

            return NavigateInFrameAsync(document, () => aimed.OpenAsync(DocumentRequest.Get(target, referer: document.DocumentUri)));
        }

        return TrackNavigationAsync(() => element.NavigateAsync(), href);
    }

    private void ScrollToFragment(IDocument document, string fragment)
    {
        var id = Uri.UnescapeDataString(fragment);
        var element = document.GetElementById(id) ?? document.GetElementsByName(id).FirstOrDefault();

        if (element is not null)
        {
            element.ScrollIntoView();
        }
        else if (id.Length == 0 || id.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            if (ReferenceEquals(document, _document))
            {
                ScrollPage(double.NegativeInfinity);
            }
            else
            {
                document.DocumentElement?.ScrollTo(0, 0);
            }
        }
    }

    private Task? HandleKeyDown(string key, InputModifiers modifiers)
    {
        if (GetKeyboardTarget() is not IElement target)
        {
            return null;
        }

        KeepAnimating(InteractionDuration);

        // the popup takes the keys, the page doesn't get to see them
        if (_selectPopup is { } popup)
        {
            HandleSelectPopupKey(popup, key, modifiers);
            return null;
        }

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

        var focused = FocusedDocument!.ActiveElement;
        var isTextEntry = FormControls.IsTextEntry(focused);

        if (key != "Tab" && IsDropdown(focused) && HandleClosedDropdownKey((IHtmlSelectElement)focused!, key, modifiers))
        {
            return null;
        }

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
            case "ArrowUp" or "ArrowDown" when focused is IHtmlSelectElement listBox && listBox.IsListBox():
                if (StepListBox(listBox, key == "ArrowUp" ? -1 : 1, modifiers))
                {
                    FireChange(listBox);
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
        var document = FocusedDocument;
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
            focusable = focusable is IHtmlLabelElement label && FormControls.GetLabeledControl(label) is { } control ? control : focusable.ParentElement;
        }

        var current = FocusedDocument?.ActiveElement;

        if (focusable != current)
        {
            if (focusable is IHtmlElement element)
            {
                (current as IHtmlElement)?.DoBlur();
                element.DoFocus();
            }
            else
            {
                (current as IHtmlElement)?.DoBlur();
            }
        }

        // what is clicked in a document has the keyboard go there
        _focusedFrameDocument = target.Owner is { } owner && !ReferenceEquals(owner, _document) ? owner : null;
    }

    private void MoveFocus(int direction)
    {
        var document = FocusedDocument!;
        var candidates = document.All
            .Where(m => FormControls.IsFocusable(m) && !(int.TryParse(m.GetAttribute("tabindex"), out var index) && index < 0))
            .ToList();

        if (candidates.Count > 0)
        {
            var current = candidates.IndexOf(document.ActiveElement!);
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
        if (!IsReadyForLayout())
        {
            return null;
        }

        for (; element is not null; element = element.ParentElement)
        {
            var document = element.Owner;

            if (element == document?.Body || element == document?.DocumentElement)
            {
                if (ReferenceEquals(document, _document) || document is null || GetFrameElement(document) is not { } frame)
                {
                    return null;
                }

                // the iframe scrolls what it shows, and once it can't what is around it does
                var root = document.DocumentElement;

                if (root is not null && frame.GetAttribute("scrolling") != "no")
                {
                    var maximum = root.GetScrollHeight() - root.GetClientHeight();
                    var position = root.GetScrollTop();

                    if ((deltaY < 0 && position > 0) || (deltaY > 0 && position < maximum))
                    {
                        return root;
                    }
                }

                // on with the iframe, which the loop moves away from
                element = frame;
            }

            var overflow = element.ComputeCurrentStyle().GetPropertyValue("overflow-y");

            if (overflow is "auto" or "scroll" || (element is IHtmlSelectElement && element.IsListBox()))
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

    private void RunScript(string code) => RunScript(_document, code);

    private void RunScript(IDocument? document, string code)
    {
        try
        {
            document?.ExecuteScript(code);
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

    private MouseEvent CreateMouseEvent(IElement target, string type, double x, double y, InputModifiers modifiers)
    {
        var (clientX, clientY) = ToClient(target, x, y);
        return CreateMouseEvent(target.Owner?.DefaultView ?? _document?.DefaultView, type, x, y, clientX, clientY, modifiers);
    }

    private static MouseEvent CreateMouseEvent(IWindow? view, string type, double x, double y, double clientX, double clientY, InputModifiers modifiers) => new(
        type,
        bubbles: true,
        cancelable: true,
        view: view,
        detail: 1,
        screenX: (int)x,
        screenY: (int)y,
        clientX: (int)clientX,
        clientY: (int)clientY,
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
