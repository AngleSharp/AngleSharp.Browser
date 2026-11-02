using System;
using System.Threading.Tasks;
using AngleSharp.Io.Dom;
using Avalonia.Input.Platform;

namespace AngleSharp.Browser.Avalonia;

/// <summary>
/// Gives pages access to the clipboard of an Avalonia top level; see
/// <see cref="BrowserOptions.ClipboardPlatform"/>.
/// </summary>
public sealed class AvaloniaClipboardPlatform : IClipboardPlatform
{
    private readonly IClipboard _clipboard;

    /// <summary>
    /// Creates the integration for the given clipboard.
    /// </summary>
    /// <param name="clipboard">The clipboard, e.g., of the window.</param>
    public AvaloniaClipboardPlatform(IClipboard clipboard)
    {
        _clipboard = clipboard;
    }

    /// <inheritdoc />
    public async Task<String> ReadTextAsync()
    {
        var content = await _clipboard.TryGetTextAsync();
        return content ?? String.Empty;
    }

    /// <inheritdoc />
    public async Task WriteTextAsync(String text)
    {
        await _clipboard.SetTextAsync(text);
    }
}
