namespace AngleSharp.Browser;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Browser.Integration;
using AngleSharp.Css;
using AngleSharp.Io;
using AngleSharp.Io.Cookie;
using AngleSharp.Io.Storage;
using AngleSharp.Renderer;
using Avalonia.Input.Platform;
using Avalonia.Platform;

class Engine
{
    private readonly IBrowsingContext _context;
    private readonly DefaultRenderDevice _device;

    public Engine(IClipboard clipboard, Screen screen)
    {
        var root = Environment.CurrentDirectory;
        var storagePath = Path.Combine(root, "storage");
        var cookiePath = Path.Combine(root, "cookies.jar");
        var options = new IoOptions
        {
            //TODO create factories for the browser
            StorageFactory = StorageProviderFactory.CreatePersistent(storagePath),
            ClipboardPlatform = new ClipboardPlatform(clipboard),
            CookieHandler = new LocalFileHandler(cookiePath),
        };
        var loader = new LoaderOptions
        {
            IsResourceLoadingEnabled = true,
        };
        var device = new DefaultRenderDevice
        {
            Category = DeviceCategory.Screen,
            ColorBits = 32,
            DeviceHeight = screen.Bounds.Height,
            DeviceWidth = screen.Bounds.Width,
            Frequency = 60, // ?
            Resolution = 96, // ?
            IsGrid = false,
            IsInterlaced = false,
            IsScripting = true,
            FontSize = 16,
            MonochromeBits = 16,
            ViewPortHeight = 0, // ?
            ViewPortWidth = 0, // ?
        };
        var config = Configuration.Default
            .WithJs()
            .WithEventLoop()
            .WithCss()
            .WithRequesters()
            .WithDefaultLoader(loader)
            .WithRenderDevice(device)
            .WithIo(options)
            .WithXml()
            .WithWasm()
            .WithRendering();
        _context = BrowsingContext.New(config);
        _device = device;
    }

    public int Height
    {
        get { return _device.DeviceHeight; }
        set { _device.DeviceHeight = value; }
    }

    public int Width
    {
        get { return _device.DeviceWidth; }
        set { _device.DeviceWidth = value; }
    }

    public async Task StreamRendersAsync(
        string url,
        TimeSpan interval,
        Func<byte[], Task> onRendered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(onRendered);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var document = await _context.OpenAsync(url, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var image = document.RenderToPng();
            await onRendered(image.Data).ConfigureAwait(false);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
