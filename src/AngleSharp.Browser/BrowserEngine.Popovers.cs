namespace AngleSharp.Browser;

using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// Popovers the user acts on: a button with a `popovertarget` shows, hides or toggles its
/// popover when activated, and a press outside of the open `auto` (and `hint`) popovers or Esc
/// closes them (light dismiss, HTML 6.12.4).
/// </summary>
partial class BrowserEngine
{
    /// <summary>
    /// Runs the popover target activation behavior of a button, if it has a target. Requires exclusive access.
    /// </summary>
    private static bool ActivatePopoverTarget(IElement element)
    {
        var (target, action) = element switch
        {
            // a button submitting or resetting its form does that instead
            IHtmlButtonElement button when button.Form is null || IsType(button.Type, "button") =>
                (button.GetPopoverTargetElement(), button.GetPopoverTargetAction()),
            IHtmlInputElement input when IsType(input.Type, "button") || (input.Form is null && (IsType(input.Type, "submit") || IsType(input.Type, "reset") || IsType(input.Type, "image"))) =>
                (input.GetPopoverTargetElement(), input.GetPopoverTargetAction()),
            _ => (null, null),
        };

        if (target is null)
        {
            return false;
        }

        var isOpen = target.IsPopoverOpen();

        try
        {
            if (isOpen && action is "hide" or "toggle")
            {
                target.HidePopover();
            }
            else if (!isOpen && action is "show" or "toggle")
            {
                target.ShowPopover();
            }
        }
        catch (DomException)
        {
            // e.g. the target is no popover (anymore), which the page doesn't get to know of
        }

        return true;
    }

    /// <summary>
    /// Closes the open popovers that light dismiss for a press on the element. Requires exclusive access.
    /// </summary>
    private static void LightDismissPopovers(IElement? pressed)
    {
        if (pressed?.Owner is not { } document)
        {
            return;
        }

        foreach (var popover in GetLightDismissedPopovers(document))
        {
            // a press in it or on what toggles it keeps it (the invoker closes it on its own)
            if (popover.Contains(pressed) || IsInvokerOf(pressed, popover))
            {
                continue;
            }

            HidePopover(popover);
        }
    }

    /// <summary>
    /// Closes the topmost light dismissed popover for Esc, if one is open. Requires exclusive access.
    /// </summary>
    private bool CloseLastPopover()
    {
        var document = FocusedDocument ?? _document;

        if (document is null || GetLightDismissedPopovers(document).LastOrDefault() is not { } popover)
        {
            return false;
        }

        HidePopover(popover);
        return true;
    }

    private static IHtmlElement[] GetLightDismissedPopovers(IDocument document) =>
        document.QuerySelectorAll("[popover]").OfType<IHtmlElement>()
            .Where(element => element.IsPopoverOpen() && element.GetPopover() is "auto" or "hint")
            .ToArray();

    private static bool IsInvokerOf(IElement element, IHtmlElement popover)
    {
        for (var current = element; current is not null; current = current.ParentElement)
        {
            var target = current switch
            {
                IHtmlButtonElement button => button.GetPopoverTargetElement(),
                IHtmlInputElement input => input.GetPopoverTargetElement(),
                _ => null,
            };

            if (target == popover)
            {
                return true;
            }
        }

        return false;
    }

    private static void HidePopover(IHtmlElement popover)
    {
        try
        {
            popover.HidePopover();
        }
        catch (DomException)
        {
        }
    }
}
