namespace AngleSharp.Browser;

using System;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// A frame laid out by <see cref="BrowserEngine.PrepareFrameAsync"/>. Rasterizing it does not
/// touch the DOM, so hosts can do it on another thread while the engine goes on.
/// </summary>
public sealed class BrowserFrame
{
    private readonly RenderedFrame _frame;
    private readonly Action _painted;

    internal BrowserFrame(RenderedFrame frame, Action painted)
    {
        _frame = frame;
        _painted = painted;
    }

    /// <summary>
    /// Gets the width of the frame in CSS pixels.
    /// </summary>
    public int Width => _frame.Width;

    /// <summary>
    /// Gets the height of the frame in CSS pixels, i.e., the viewport and the overscan.
    /// </summary>
    public int Height => _frame.Height;

    /// <summary>
    /// Gets the rows painted above the viewport: the viewport starts at this row.
    /// </summary>
    public int OverscanTop => _frame.OverscanTop;

    /// <summary>
    /// Gets the vertical scroll offset of the page the frame was painted at.
    /// </summary>
    public double ScrollTop => _frame.ScrollTop;

    /// <summary>
    /// Gets if the frame may be shifted to show another scroll offset (see
    /// <see cref="BrowserEngine.ScrollPosition"/>) until the next frame arrives, which holds
    /// unless the page has fixed or sticky boxes.
    /// </summary>
    public bool IsScrollShiftable => _frame.IsScrollShiftable;

    /// <summary>
    /// Rasterizes the frame. Safe to call from any thread.
    /// </summary>
    /// <returns>The premultiplied RGBA pixels of the frame.</returns>
    public RenderedPixels Rasterize()
    {
        var pixels = _frame.Rasterize();
        _painted();
        return pixels;
    }
}
