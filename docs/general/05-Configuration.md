---
title: "Configuration"
section: "AngleSharp.Browser"
---
# Configuration

Everything that touches the outside world is configured via `BrowserOptions`, which is passed to the constructor of the engine.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `ProfileDirectory` | `null` | Where cookies, web storage and the HTTP cache are persisted; `null` keeps everything in memory. |
| `UserAgent` | `null` | The user agent to report; `null` uses the default of AngleSharp.Io. |
| `DefaultHeaders` | empty | Headers added to every request, unless a request sets them already. |
| `ColorScheme` | `null` | The answer to `prefers-color-scheme`, e.g., `light` or `dark`. |
| `IsScriptingEnabled` | `true` | If JavaScript should be evaluated. |
| `ClipboardPlatform` | `null` | The clipboard exposed to pages; `null` means the API is not available. |
| `ViewportWidth`, `ViewportHeight` | 1280, 800 | The initial viewport in CSS pixels. |
| `ScreenWidth`, `ScreenHeight` | 1920, 1080 | The screen size reported to media queries. |
| `FontSize` | 16 | The default font size in CSS pixels. |
| `ConfigureIo` | `null` | A callback customizing the IO options. |
| `Configure` | `null` | A callback extending the AngleSharp configuration. |
| `SchemeHandlers` | empty | Handlers answering the requests of custom URL schemes. |

```cs
var options = new BrowserOptions
{
    ProfileDirectory = "profile",
    UserAgent = "MyApp/1.0",
    ColorScheme = "dark",
    ViewportWidth = 1024,
    ViewportHeight = 768,
};

options.DefaultHeaders["Accept-Language"] = "de";

using var engine = new BrowserEngine(options);
```

## Profiles

Without a `ProfileDirectory` nothing survives the engine, which is what you want for tests and automation. With one, a later engine using the same directory finds its cookies, storage and cache again.

## Customizing IO and AngleSharp

`ConfigureIo` runs after the defaults have been derived from the other options. It gives access to all of `IoOptions` from AngleSharp.Io, e.g., the HTTP handler:

```cs
using System.Net;

var options = new BrowserOptions
{
    ConfigureIo = io => io.HttpHandler.Proxy = new WebProxy("http://localhost:8080"),
};
```

Like a browser, the handler decompresses responses (gzip, deflate and brotli; `AutomaticDecompression`), which some servers send even if not asked to.

Pages can register service workers (`navigator.serviceWorker`, through `ServiceWorkerPlatform`). The workers are installed and activated, but their scripts don't run, so every request goes to the network. Setting `io.ServiceWorkerPlatform` to `null` in `ConfigureIo` removes the API, and another platform can run the workers.

`Configure` receives the AngleSharp configuration before the browsing context is created, so further services can be added:

```cs
using AngleSharp;

var options = new BrowserOptions
{
    Configure = config => config.WithLocaleBasedEncoding(),
};
```

## Custom Schemes

`SchemeHandlers` registers URL schemes of the host, e.g., for its own pages. A handler gets the request and answers a response, or `null` if there is nothing at the address. Pages can link to these addresses, and `NavigateAsync` keeps them as they are (`Schemes` lists them; `BrowserEngine.NormalizeAddress(address, schemes)` does the same for an address bar):

```cs
using System.IO;
using System.Net;
using System.Text;
using AngleSharp.Io;

var options = new BrowserOptions();
options.SchemeHandlers["app"] = (request, cancellationToken) =>
{
    var response = new DefaultResponse
    {
        Address = request.Address,
        StatusCode = HttpStatusCode.OK,
        Content = new MemoryStream(Encoding.UTF8.GetBytes($"<h1>{request.Address.Data}</h1>")),
    };
    response.Headers[HeaderNames.ContentType] = "text/html; charset=utf-8";
    return Task.FromResult<IResponse?>(response);
};

using var engine = new BrowserEngine(options);
await engine.NavigateAsync("app:settings");
```

The handlers are asked before the built-in requesters, so a handler for `http` would take over the network.

## Multiple Windows

One engine stands for one window. When a page asks for another window (a link with `target="_blank"`, or a link clicked with Control), the engine raises `WindowRequested`. If the request is not marked as handled the page navigates in place, so there is nothing to do if a host only has a single window.

`CreateWindow` creates the engine of another window. It has its own browsing context and history, but shares the profile (cookies, storage, caches), even without a profile directory:

```cs
engine.WindowRequested += (_, request) =>
{
    // must be set while handling the event, which is raised on the engine's thread
    request.Handled = true;

    _ = Task.Run(async () =>
    {
        var window = engine.CreateWindow();
        await window.NavigateAsync(request.Url);
    });
};
```

The caller owns the returned engine and has to dispose it. An overload takes other options for the new window, e.g., after settings changed.
