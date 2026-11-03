namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;

partial class BrowserEngine
{
    // the rows Page Up and Page Down move by in a dropdown
    private const int SelectPageSize = 3;
    // typing again after this long starts a new search
    private const long TypeaheadResetMilliseconds = 1000;

    private sealed record OpenSelectPopup(IHtmlSelectElement Select, IElement? Highlighted, SelectPopup View, int ViewportWidth, int ViewportHeight);

    private volatile OpenSelectPopup? _selectPopup;
    private string _typeahead = string.Empty;
    private long _lastTypeahead;

    /// <summary>
    /// Gets if the list of a dropdown is open, which every frame shows on top of the page.
    /// </summary>
    public bool IsSelectPopupOpen => _selectPopup is not null;

    private static bool IsDropdown(IElement? element) =>
        element is IHtmlSelectElement { IsDisabled: false } select && !select.IsListBox();

    private void ShowSelectPopup(IHtmlSelectElement select)
    {
        if (select.Options.Length == 0)
        {
            return;
        }

        var highlighted = (IElement?)select.SelectedOptions.LastOrDefault();

        if (CreatePopupView(select, highlighted, 0) is { } view)
        {
            // opens with the selected option in view
            var scrollTop = highlighted is null ? 0 : view.GetScrollTopShowing(highlighted);
            SetSelectPopup(select, highlighted, scrollTop);
        }
    }

    private void SetSelectPopup(IHtmlSelectElement select, IElement? highlighted, double scrollTop)
    {
        var view = CreatePopupView(select, highlighted, scrollTop);
        _selectPopup = view is null ? null : new OpenSelectPopup(select, highlighted, view, ViewportWidth, ViewportHeight);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    // one in an iframe opens in the viewport of the page
    private SelectPopup? CreatePopupView(IHtmlSelectElement select, IElement? highlighted, double scrollTop)
    {
        var (x, y) = ToClient(select, 0d, 0d);
        return SelectPopup.Create(select, highlighted, scrollTop, -x, -y, _harness.RenderDevice);
    }

    private void CloseSelectPopup()
    {
        if (_selectPopup is not null)
        {
            _selectPopup = null;
            Invalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Selects a row of the open popup, which closes it, as browsers do it.
    /// </summary>
    private void CommitSelectPopup(OpenSelectPopup popup, IElement? row)
    {
        _selectPopup = null;
        Invalidated?.Invoke(this, EventArgs.Empty);

        if (FormControls.IsSelectableOption(row) && FormControls.SetSelectedOptions(popup.Select, [row!]))
        {
            FireChange(popup.Select);
        }

        Fire(popup.Select, new AngleSharp.Dom.Events.Event("click", true, true));
    }

    /// <summary>
    /// What a frame needs to show of the popup: as the page is now, or nothing if the select
    /// isn't there to open it anymore. Requires exclusive access.
    /// </summary>
    private SelectPopup? RefreshSelectPopup()
    {
        if (_selectPopup is not { } popup)
        {
            return null;
        }

        if (popup.ViewportWidth != ViewportWidth || popup.ViewportHeight != ViewportHeight ||
            !IsInPage(popup.Select.Owner) || popup.Select.Owner?.DocumentElement?.Contains(popup.Select) != true || popup.Select.IsDisabled ||
            FocusedDocument?.ActiveElement != popup.Select || !IsDropdown(popup.Select))
        {
            _selectPopup = null;
            return null;
        }

        var view = SelectPopup.Create(popup.Select, popup.Highlighted, popup.View.ScrollTop);

        if (view is null)
        {
            _selectPopup = null;
            return null;
        }

        _selectPopup = popup with { View = view };
        return view;
    }

    /// <summary>
    /// Handles a key while the popup is open, which takes all of them. Requires exclusive access.
    /// </summary>
    private void HandleSelectPopupKey(OpenSelectPopup popup, string key, InputModifiers modifiers)
    {
        var options = popup.Select.Options.OfType<IElement>().ToList();

        switch (key)
        {
            case "Escape":
                CloseSelectPopup();
                return;
            case "Enter" or "Tab" or " ":
                CommitSelectPopup(popup, popup.Highlighted);
                return;
            case "ArrowUp" when modifiers.HasFlag(InputModifiers.Alt):
            case "ArrowDown" when modifiers.HasFlag(InputModifiers.Alt):
                CommitSelectPopup(popup, popup.Highlighted);
                return;
            default:
                if (FindSelectTarget(options, popup.Highlighted, key, modifiers) is { } next)
                {
                    SetSelectPopup(popup.Select, next, popup.View.GetScrollTopShowing(next));
                }

                return;
        }
    }

    /// <summary>
    /// Handles a key on a closed dropdown. Requires exclusive access.
    /// </summary>
    /// <returns>True if the key was for the dropdown.</returns>
    private bool HandleClosedDropdownKey(IHtmlSelectElement select, string key, InputModifiers modifiers)
    {
        if (key is "Enter" or " " ||
            (key is "ArrowUp" or "ArrowDown" && modifiers.HasFlag(InputModifiers.Alt)))
        {
            ShowSelectPopup(select);
            return true;
        }

        var options = select.Options.OfType<IElement>().ToList();
        var next = FindSelectTarget(options, select.SelectedOptions.LastOrDefault(), key, modifiers);

        if (next is null)
        {
            return key is "ArrowUp" or "ArrowDown" or "Home" or "End" or "PageUp" or "PageDown" || IsTypeaheadKey(key, modifiers);
        }

        if (FormControls.SetSelectedOptions(select, [next]))
        {
            FireChange(select);
        }

        return true;
    }

    private static bool IsTypeaheadKey(string key, InputModifiers modifiers) =>
        key.Length == 1 && !char.IsControl(key[0]) &&
        !modifiers.HasFlag(InputModifiers.Control) && !modifiers.HasFlag(InputModifiers.Meta) && !modifiers.HasFlag(InputModifiers.Alt);

    /// <summary>
    /// The option a key moves to from the current one, if any; disabled ones are skipped.
    /// </summary>
    private IElement? FindSelectTarget(List<IElement> options, IElement? current, string key, InputModifiers modifiers)
    {
        var index = current is null ? -1 : options.IndexOf(current);

        switch (key)
        {
            case "ArrowDown":
                return FindSelectable(options, index + 1, 1);
            case "ArrowUp":
                return FindSelectable(options, index < 0 ? options.Count - 1 : index - 1, -1);
            case "Home":
                return FindSelectable(options, 0, 1);
            case "End":
                return FindSelectable(options, options.Count - 1, -1);
            case "PageDown":
                return FindSelectable(options, Math.Min(index + SelectPageSize, options.Count - 1), 1) ?? FindSelectable(options, options.Count - 1, -1);
            case "PageUp":
                return FindSelectable(options, Math.Max(index - SelectPageSize, 0), -1) ?? FindSelectable(options, 0, 1);
        }

        return IsTypeaheadKey(key, modifiers) ? FindTypeaheadTarget(options, index, key) : null;
    }

    private static IElement? FindSelectable(List<IElement> options, int start, int delta)
    {
        for (var index = start; index >= 0 && index < options.Count; index += delta)
        {
            if (FormControls.IsSelectableOption(options[index]))
            {
                return options[index];
            }
        }

        return null;
    }

    /// <summary>
    /// The option the typed text leads to: a further key extends what is searched for, a single
    /// one (repeated) goes through the options starting with it.
    /// </summary>
    private IElement? FindTypeaheadTarget(List<IElement> options, int index, string key)
    {
        var now = Environment.TickCount64;

        if (now - _lastTypeahead > TypeaheadResetMilliseconds)
        {
            _typeahead = string.Empty;
        }

        _lastTypeahead = now;
        _typeahead += key;
        var isRepeated = _typeahead.All(character => char.ToLowerInvariant(character) == char.ToLowerInvariant(_typeahead[0]));
        var search = isRepeated ? _typeahead[..1] : _typeahead;
        var start = isRepeated ? index + 1 : Math.Max(index, 0);

        for (var offset = 0; offset < options.Count; offset++)
        {
            var option = options[(start + offset) % options.Count];

            if (FormControls.IsSelectableOption(option) &&
                (option.TextContent ?? string.Empty).Trim().StartsWith(search, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return null;
    }
}
