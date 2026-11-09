namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// Helpers for interacting with form controls. AngleSharp.Renderer paints
/// form controls from their attributes (value, checked, selected) and text
/// content, so any live state changed by user input is mirrored there.
/// </summary>
static class FormControls
{
    private static readonly HashSet<string> TextInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "search", "url", "tel", "email", "password", "number",
    };

    public static bool IsTextEntry(IElement? element) => element switch
    {
        IHtmlInputElement input => TextInputTypes.Contains(input.Type) && !input.IsDisabled && !input.IsReadOnly,
        IHtmlTextAreaElement area => !area.IsDisabled && !area.IsReadOnly,
        _ => false,
    };

    /// <summary>
    /// The control of a label: the one its `for` attribute names, else its first labelable
    /// descendant (HTML 4.10.4). AngleSharp only knows the former so far.
    /// </summary>
    public static IElement? GetLabeledControl(IHtmlLabelElement label)
    {
        if (label.Control is { } control)
        {
            return control;
        }

        return label.HasAttribute("for")
            ? null
            : label.Descendants<IElement>().FirstOrDefault(IsLabelable);
    }

    private static bool IsLabelable(IElement element) => element switch
    {
        IHtmlInputElement input => !input.Type.Equals("hidden", StringComparison.OrdinalIgnoreCase),
        IHtmlButtonElement or IHtmlSelectElement or IHtmlTextAreaElement or IHtmlMeterElement or IHtmlOutputElement or IHtmlProgressElement => true,
        _ => false,
    };

    public static bool IsToggle(IElement? element) =>
        element is IHtmlInputElement input && (input.Type.Equals("checkbox", StringComparison.OrdinalIgnoreCase) || input.Type.Equals("radio", StringComparison.OrdinalIgnoreCase));

    public static bool IsFocusable(IElement element) => element switch
    {
        IHtmlInputElement input => !input.IsDisabled && !input.Type.Equals("hidden", StringComparison.OrdinalIgnoreCase),
        IHtmlTextAreaElement area => !area.IsDisabled,
        IHtmlSelectElement select => !select.IsDisabled,
        IHtmlButtonElement button => !button.IsDisabled,
        IHtmlAnchorElement anchor => anchor.HasAttribute("href"),
        _ => false,
    };

    public static string GetValue(IElement element) => element switch
    {
        IHtmlInputElement input => input.Value,
        IHtmlTextAreaElement area => area.Value,
        _ => string.Empty,
    };

    public static void SetValue(IElement element, string value)
    {
        switch (element)
        {
            case IHtmlInputElement input:
                input.SetAttribute("value", value);
                input.Value = value;
                break;
            case IHtmlTextAreaElement area:
                area.TextContent = value;
                area.Value = value;
                break;
        }
    }

    public static void SetChecked(IHtmlInputElement input, bool value)
    {
        input.IsChecked = value;

        if (input.Type.Equals("radio", StringComparison.OrdinalIgnoreCase) && value && !string.IsNullOrEmpty(input.Name))
        {
            var scope = (IParentNode?)input.Form ?? input.Owner;
            var group = scope?.QuerySelectorAll("input[type=radio]")
                .OfType<IHtmlInputElement>()
                .Where(m => m.Name == input.Name) ?? Enumerable.Empty<IHtmlInputElement>();

            foreach (var other in group)
            {
                SyncCheckedAttribute(other, other == input);
            }
        }
        else
        {
            SyncCheckedAttribute(input, value);
        }
    }

    /// <summary>
    /// If an option can be chosen: neither it nor its option group is disabled.
    /// </summary>
    public static bool IsSelectableOption(IElement? element) =>
        element is IHtmlOptionElement option && !option.HasAttribute("disabled") &&
        option.ParentElement is not IHtmlOptionsGroupElement { IsDisabled: true };

    /// <summary>
    /// Makes the given options the selected ones of a select.
    /// </summary>
    /// <returns>True if that changed what is selected.</returns>
    public static bool SetSelectedOptions(IHtmlSelectElement select, IReadOnlyCollection<IElement> selected)
    {
        var hasChanged = false;

        foreach (var option in select.Options.OfType<IHtmlOptionElement>())
        {
            var isSelected = selected.Contains(option);
            hasChanged |= isSelected != option.IsSelected;
            option.IsSelected = isSelected;

            if (isSelected)
            {
                option.SetAttribute("selected", string.Empty);
            }
            else
            {
                option.RemoveAttribute("selected");
            }
        }

        return hasChanged;
    }

    /// <summary>
    /// The selectable options from one option to another, in either direction.
    /// </summary>
    public static List<IElement> GetOptionRange(IHtmlSelectElement select, IElement from, IElement to)
    {
        var options = select.Options.OfType<IElement>().ToList();
        var start = options.IndexOf(from);
        var end = options.IndexOf(to);
        return options.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).Where(IsSelectableOption).ToList();
    }

    private static void SyncCheckedAttribute(IHtmlInputElement input, bool value)
    {
        input.IsChecked = value;

        if (value)
        {
            input.SetAttribute("checked", string.Empty);
        }
        else
        {
            input.RemoveAttribute("checked");
        }
    }
}
