namespace AngleSharp.Browser;

using System;
using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;

/// <summary>
/// Sliders (`input[type=range]`): a press sets the value at the pointer and the slider follows
/// the pointer while the button is held, firing `input` for each new value and `change` once
/// released; the arrow keys, Page Up/Down, Home and End step it while it has the focus.
/// </summary>
partial class BrowserEngine
{
    // the slider dragged while the button is held, with the value it had when pressed
    private IHtmlInputElement? _slidingRange;
    private string? _slidingRangeStart;

    private static bool IsRange(IElement? element) =>
        element is IHtmlInputElement { IsDisabled: false } input && input.Type.Equals("range", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Acts on a press on a slider, if it was one. Requires exclusive access.
    /// </summary>
    private bool PressRange(IElement? target, double x)
    {
        if (!IsRange(target))
        {
            return false;
        }

        var input = (IHtmlInputElement)target!;
        _slidingRange = input;
        _slidingRangeStart = input.Value;
        SlideRange(x);
        return true;
    }

    /// <summary>
    /// Moves the dragged slider to the position of the pointer. Requires exclusive access.
    /// </summary>
    private void SlideRange(double x)
    {
        if (_slidingRange is not { Owner: not null } input)
        {
            return;
        }

        var box = input.GetBoundingClientRect();
        var style = input.ComputeCurrentStyle();
        var left = box.Left + ParsePixels(style.GetPropertyValue("border-left-width")) + ParsePixels(style.GetPropertyValue("padding-left"));
        var width = box.Width - (left - box.Left) - ParsePixels(style.GetPropertyValue("border-right-width")) - ParsePixels(style.GetPropertyValue("padding-right"));

        if (width <= 0)
        {
            return;
        }

        var (min, max, _) = GetRangeBounds(input);
        var fraction = Math.Clamp((ToClient(input, x, 0).X - left) / width, 0d, 1d);
        SetRangeValue(input, min + (fraction * (max - min)));
    }

    /// <summary>
    /// Ends dragging a slider: its change is committed. Requires exclusive access.
    /// </summary>
    private void ReleaseRange()
    {
        if (_slidingRange is { } input)
        {
            _slidingRange = null;

            if (input.Value != _slidingRangeStart)
            {
                Fire(input, new Event("change", true, false));
            }
        }
    }

    /// <summary>
    /// Steps the focused slider for a key, if it is one. Requires exclusive access.
    /// </summary>
    private bool HandleRangeKey(IElement? focused, string key)
    {
        if (!IsRange(focused))
        {
            return false;
        }

        var input = (IHtmlInputElement)focused!;
        var (min, max, step) = GetRangeBounds(input);
        var value = ParseRangeNumber(input.Value, (min + max) / 2);
        var large = Math.Max(step, (max - min) / 10);
        var target = key switch
        {
            "ArrowRight" or "ArrowUp" => value + step,
            "ArrowLeft" or "ArrowDown" => value - step,
            "PageUp" => value + large,
            "PageDown" => value - large,
            "Home" => min,
            "End" => max,
            _ => double.NaN,
        };

        if (double.IsNaN(target))
        {
            return false;
        }

        if (SetRangeValue(input, target))
        {
            Fire(input, new Event("change", true, false));
        }

        return true;
    }

    /// <summary>
    /// Sets the value of a slider, snapped to its step, firing `input` if it changed.
    /// </summary>
    private bool SetRangeValue(IHtmlInputElement input, double value)
    {
        var (min, max, step) = GetRangeBounds(input);

        if (step > 0)
        {
            value = min + (Math.Round((value - min) / step) * step);

            // a step not fitting the range ends below the maximum
            if (value > max)
            {
                value -= step;
            }
        }

        value = Math.Clamp(value, min, max);
        var text = Math.Round(value, 10).ToString("R", CultureInfo.InvariantCulture);

        if (text == input.Value)
        {
            return false;
        }

        FormControls.SetValue(input, text);
        Fire(input, new Event("input", true, false));
        return true;
    }

    /// <summary>
    /// The minimum, maximum and step of a slider, which default to 0, 100 and 1 (HTML 4.10.5.1.13);
    /// a step of 0 is `any`.
    /// </summary>
    private static (double Min, double Max, double Step) GetRangeBounds(IHtmlInputElement input)
    {
        var min = ParseRangeNumber(input.GetAttribute("min"), 0);
        var max = ParseRangeNumber(input.GetAttribute("max"), 100);
        var stepText = input.GetAttribute("step");
        var step = string.Equals(stepText?.Trim(), "any", StringComparison.OrdinalIgnoreCase) ? 0 : ParseRangeNumber(stepText, 1) is > 0 and var given ? given : 1;
        return (min, Math.Max(min, max), step);
    }

    private static double ParseRangeNumber(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
}
