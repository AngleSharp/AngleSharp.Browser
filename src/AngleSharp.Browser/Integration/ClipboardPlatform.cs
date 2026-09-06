namespace AngleSharp.Browser.Integration;

using System;
using System.Threading.Tasks;
using AngleSharp.Io.Dom;
using Avalonia.Input.Platform;

class ClipboardPlatform : IClipboardPlatform
{
    private readonly IClipboard _clipboard;

    public ClipboardPlatform(IClipboard clipboard)
    {
        _clipboard = clipboard;
    }

    public async Task<String> ReadTextAsync()
    {
        var content = await _clipboard.TryGetTextAsync();
        return content ?? String.Empty;
    }

    public async Task WriteTextAsync(String text)
    {
        await _clipboard.SetTextAsync(text);
    }
}
