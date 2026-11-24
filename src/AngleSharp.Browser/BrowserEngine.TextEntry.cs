namespace AngleSharp.Browser;

using System;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Html.Dom;

/// <summary>
/// Editing the text of the focused text field (`input` or `textarea`) at its cursor, which is
/// the field's `selectionStart`/`selectionEnd` (AngleSharp.Renderer paints the caret and the
/// selected text there): the arrow keys, Home and End move the cursor (by words with Ctrl or
/// Alt, over the whole text with Ctrl+Home/End, extending the selection with Shift), text
/// replaces the selection, Backspace and Delete remove it or the character before or after it.
/// </summary>
partial class BrowserEngine
{
    // the field the engine last edited with its value and selection then: a value changed by a
    // script while the selection stayed moves the cursor to its end (AngleSharp before 1.11
    // keeps the cursor where it was)
    private IElement? _editedField;
    private string? _editedValue;
    private (int Start, int End) _editedSelection;

    /// <summary>
    /// Acts on a key pressed in the focused text field, if the key edits text or moves the
    /// cursor. Requires exclusive access.
    /// </summary>
    private bool HandleTextEntryKey(IElement field, string key, InputModifiers modifiers)
    {
        var value = FormControls.GetValue(field);
        var (start, end, backward) = GetFieldSelection(field, value);
        var anchor = backward ? end : start;
        var focus = backward ? start : end;
        var extend = modifiers.HasFlag(InputModifiers.Shift);
        var byWord = modifiers.HasFlag(InputModifiers.Control) || modifiers.HasFlag(InputModifiers.Alt);
        var command = modifiers.HasFlag(InputModifiers.Meta);
        var isMultiline = field is IHtmlTextAreaElement;
        int target;

        switch (key)
        {
            case "a" or "A" when modifiers.HasFlag(InputModifiers.Control) || command:
                SetFieldSelection(field, value, 0, value.Length);
                return true;
            case "ArrowLeft" when command:
            case "Home":
                target = (byWord && key == "Home") || !isMultiline ? 0 : GetLineStart(value, focus);
                break;
            case "ArrowRight" when command:
            case "End":
                target = (byWord && key == "End") || !isMultiline ? value.Length : GetLineEnd(value, focus);
                break;
            case "ArrowUp" when command:
                target = 0;
                break;
            case "ArrowDown" when command:
                target = value.Length;
                break;
            case "ArrowLeft":
                // without Shift a selection collapses to its start
                target = start != end && !extend ? start : byWord ? GetPreviousWordStart(value, focus) : GetPreviousOffset(value, focus);
                break;
            case "ArrowRight":
                target = start != end && !extend ? end : byWord ? GetNextWordEnd(value, focus) : GetNextOffset(value, focus);
                break;
            case "ArrowUp" when isMultiline:
                target = GetOffsetOnAdjacentLine(value, focus, -1);
                break;
            case "ArrowDown" when isMultiline:
                target = GetOffsetOnAdjacentLine(value, focus, 1);
                break;
            case "Backspace":
                if (start == end)
                {
                    start = byWord ? GetPreviousWordStart(value, start) : GetPreviousOffset(value, start);
                }

                ReplaceFieldText(field, value, start, end, string.Empty);
                return true;
            case "Delete":
                if (start == end)
                {
                    end = byWord ? GetNextWordEnd(value, end) : GetNextOffset(value, end);
                }

                ReplaceFieldText(field, value, start, end, string.Empty);
                return true;
            default:
                return false;
        }

        SetFieldSelection(field, value, extend ? anchor : target, target);
        return true;
    }

    private void InsertText(IElement element, string text)
    {
        var value = FormControls.GetValue(element);
        var (start, end, _) = GetFieldSelection(element, value);

        if (element is IHtmlInputElement input)
        {
            text = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
            var room = input.MaxLength >= 0 ? input.MaxLength - (value.Length - (end - start)) : int.MaxValue;
            text = text.Length > room ? text[..Math.Max(0, room)] : text;
        }

        if (text.Length > 0)
        {
            ReplaceFieldText(element, value, start, end, text);
        }
    }

    /// <summary>
    /// Puts the cursor of a text field the user focused at the end of its text (or selects
    /// all of it), as the press isn't placed within the text.
    /// </summary>
    private void PlaceCursorOnFocus(IElement? element, bool selectAll)
    {
        if (FormControls.IsTextEntry(element))
        {
            var value = FormControls.GetValue(element!);
            SetFieldSelection(element!, value, selectAll ? 0 : value.Length, value.Length);
        }
    }

    private void ReplaceFieldText(IElement field, string value, int start, int end, string text)
    {
        if (start == end && text.Length == 0)
        {
            return;
        }

        var newValue = string.Concat(value.AsSpan(0, start), text, value.AsSpan(end));
        FormControls.SetValue(field, newValue);
        var cursor = start + text.Length;
        SetFieldSelection(field, newValue, cursor, cursor);
        Fire(field, new Event("input", true, false));
    }

    private (int Start, int End, bool Backward) GetFieldSelection(IElement field, string value)
    {
        var (start, end, direction) = field switch
        {
            IHtmlInputElement input => (input.SelectionStart, input.SelectionEnd, input.SelectionDirection),
            IHtmlTextAreaElement area => (area.SelectionStart, area.SelectionEnd, area.SelectionDirection),
            _ => (value.Length, value.Length, null),
        };

        if (ReferenceEquals(field, _editedField) && !string.Equals(value, _editedValue, StringComparison.Ordinal) && (start, end) == _editedSelection)
        {
            start = end = value.Length;
        }

        start = Math.Clamp(start, 0, value.Length);
        end = Math.Clamp(end, start, value.Length);
        return (start, end, direction == "backward");
    }

    /// <summary>
    /// Selects the text between the anchor (where the selection began) and the focus (where the
    /// cursor is), which may come first.
    /// </summary>
    private void SetFieldSelection(IElement field, string value, int anchor, int focus)
    {
        var (start, end, direction) = focus < anchor ? (focus, anchor, "backward") : (anchor, focus, "forward");

        switch (field)
        {
            case IHtmlInputElement input:
                input.Select(start, end, direction);
                break;
            case IHtmlTextAreaElement area:
                area.Select(start, end, direction);
                break;
        }

        _editedField = field;
        _editedValue = value;
        _editedSelection = (start, end);

        // the caret moved without the DOM changing, the next frame shows it
        NotifyStateChanged(field);
    }

    private static int GetPreviousOffset(string value, int offset) =>
        offset <= 0 ? 0 : offset >= 2 && char.IsSurrogatePair(value[offset - 2], value[offset - 1]) ? offset - 2 : offset - 1;

    private static int GetNextOffset(string value, int offset) =>
        offset >= value.Length ? value.Length : offset + 1 < value.Length && char.IsSurrogatePair(value[offset], value[offset + 1]) ? offset + 2 : offset + 1;

    private static int GetPreviousWordStart(string value, int offset)
    {
        while (offset > 0 && !IsWordCharacter(value[offset - 1]))
        {
            offset--;
        }

        while (offset > 0 && IsWordCharacter(value[offset - 1]))
        {
            offset--;
        }

        return offset;
    }

    private static int GetNextWordEnd(string value, int offset)
    {
        while (offset < value.Length && !IsWordCharacter(value[offset]))
        {
            offset++;
        }

        while (offset < value.Length && IsWordCharacter(value[offset]))
        {
            offset++;
        }

        return offset;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c);

    private static int GetLineStart(string value, int offset) =>
        offset > 0 ? value.LastIndexOf('\n', offset - 1) + 1 : 0;

    private static int GetLineEnd(string value, int offset)
    {
        var end = value.IndexOf('\n', offset);
        return end < 0 ? value.Length : end;
    }

    /// <summary>
    /// The offset in the line above or below the one of the given offset (by line breaks, not
    /// how the lines wrap) with the same column, or the start or end of the text without one.
    /// </summary>
    private static int GetOffsetOnAdjacentLine(string value, int offset, int direction)
    {
        var lineStart = GetLineStart(value, offset);
        var column = offset - lineStart;

        if (direction < 0)
        {
            if (lineStart == 0)
            {
                return 0;
            }

            var previousStart = GetLineStart(value, lineStart - 1);
            return Math.Min(previousStart + column, lineStart - 1);
        }

        var lineEnd = GetLineEnd(value, offset);

        if (lineEnd >= value.Length)
        {
            return value.Length;
        }

        var nextStart = lineEnd + 1;
        return Math.Min(nextStart + column, GetLineEnd(value, nextStart));
    }
}
