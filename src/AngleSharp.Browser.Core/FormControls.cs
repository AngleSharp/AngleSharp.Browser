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

    public static bool StepSelection(IHtmlSelectElement select, int delta)
    {
        var options = select.Options;
        var index = Math.Clamp(select.SelectedIndex + delta, 0, options.Length - 1);

        if (options.Length == 0 || index == select.SelectedIndex)
        {
            return false;
        }

        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i]!;
            option.IsSelected = i == index;

            if (i == index)
            {
                option.SetAttribute("selected", string.Empty);
            }
            else
            {
                option.RemoveAttribute("selected");
            }
        }

        return true;
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
