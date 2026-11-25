namespace AngleSharp.Browser;

using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System;
using System.Globalization;
using System.Linq;

/// <summary>
/// The render device of the document of an iframe. The renderer sets its viewport to the size
/// the iframe was laid out with, but a script resizing the iframe expects its media queries to
/// follow right away (Acid3 test 46), as a browser lays out to answer them. A size of the iframe
/// that is known without a layout (a length in pixels) is therefore taken from its style.
/// </summary>
sealed class FrameRenderDevice(IBrowsingContext context) : DefaultRenderDevice, IRenderDevice, IRenderDimensions
{
    [ThreadStatic]
    private static bool s_isResolving;

    int IRenderDevice.ViewPortWidth => Resolve("width") ?? ViewPortWidth;

    int IRenderDevice.ViewPortHeight => Resolve("height") ?? ViewPortHeight;

    double IRenderDimensions.RenderWidth => ((IRenderDevice)this).ViewPortWidth;

    double IRenderDimensions.RenderHeight => ((IRenderDevice)this).ViewPortHeight;

    private int? Resolve(string dimension)
    {
        // computing the style of the iframe must not ask for this again (e.g. via a layout)
        if (s_isResolving || context.Parent?.Active is not { DefaultView: { } window } parent)
        {
            return null;
        }

        s_isResolving = true;

        try
        {
            var frame = parent.QuerySelectorAll("iframe")
                .OfType<IHtmlInlineFrameElement>()
                .FirstOrDefault(m => ReferenceEquals(m.ContentDocument?.Context, context));

            if (frame is null)
            {
                return null;
            }

            var style = window.GetComputedStyle(frame);

            if (!string.Equals(style.GetPropertyValue("box-sizing"), "content-box", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(style.GetPropertyValue("box-sizing")))
            {
                return null;
            }

            var value = style.GetPropertyValue(dimension)?.Trim();

            if (value is not null && value.EndsWith("px", StringComparison.Ordinal) &&
                double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels >= 0)
            {
                return (int)Math.Round(pixels);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            s_isResolving = false;
        }
    }
}
