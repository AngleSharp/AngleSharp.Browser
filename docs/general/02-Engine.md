---
title: "Browser Engine"
section: "AngleSharp.Browser"
---
# Browser Engine

The `BrowserEngine` represents a browsing context with its history, i.e., what a single browser window (or tab) would be. It is `IDisposable`; dispose it once the window goes away.

## Navigation

| Member | Description |
| --- | --- |
| `NavigateAsync(address)` | Loads an address and adds it to the history. |
| `LoadHtmlAsync(html, address)` | Shows the given markup, optionally pretending it came from the given address. |
| `ReloadAsync()` | Loads the current address again. |
| `GoBackAsync()`, `GoForwardAsync()` | Moves through the history. |
| `Url`, `Title`, `IsLoading` | The state of the current page. |
| `CanGoBack`, `CanGoForward` | If there is something to go to. |

The address goes through `BrowserEngine.NormalizeAddress`, so what a user enters in an address bar works: `example.org` turns into `https://example.org`, while rooted paths of existing files turn into `file:` addresses.

`Url` shows where the engine is going as soon as a navigation starts, like in any browser. A failed navigation does not throw: it raises `Error`, is recorded in the diagnostics, and shows an error page instead.

```cs
engine.StateChanged += (_, _) => Console.WriteLine($"{engine.Title} ({engine.Url}), loading: {engine.IsLoading}");

await engine.NavigateAsync("example.org");
Console.WriteLine(engine.CanGoBack);
```

## Threading

AngleSharp is not thread-safe, so all DOM work is serialized and runs on AngleSharp's event loop. This has some consequences:

- All `*Async` methods of the engine may be called from any thread. They wait for the engine to be free.
- Code touching the DOM has to run in `InvokeAsync`, which also makes `Document` safe to use. Handlers of DOM events run on the event loop already.
- Engine events may be raised on any thread. A UI has to dispatch to its UI thread when handling them.
- Do not block the event loop (e.g., waiting for another engine operation in `InvokeAsync`): it would wait for itself.

```cs
var links = await engine.InvokeAsync(document => document?.QuerySelectorAll("a").Length ?? 0);
Console.WriteLine(links);
```

## Working With the DOM

`InvokeAsync` gets the current document, which is the regular AngleSharp DOM. CSS and script integration work as in AngleSharp.Css and AngleSharp.Js:

```cs
using AngleSharp.Dom;

await engine.InvokeAsync(document =>
{
    var heading = document?.QuerySelector("h1");

    if (heading is not null)
    {
        heading.TextContent = "Changed from .NET";
        heading.AddEventListener("click", (_, _) => Console.WriteLine("Clicked!"));
    }
});
```

Changing the DOM invalidates the page, i.e., hosts paint a new frame on their own.

The underlying `IBrowsingContext` is available via `Context`, e.g., to retrieve services.

## Events

| Event | Raised when |
| --- | --- |
| `Invalidated` | The visual output changed and a new frame should be painted. |
| `StateChanged` | The address, title, loading state, or history state changed. |
| `HoverChanged` | The hovered link or the cursor changed. |
| `ScrollChanged` | The scroll position changed. |
| `WindowRequested` | The page asks for a new window. See [Configuration](05-Configuration.md#multiple-windows). |
| `Error` | An operation failed, e.g., a navigation or a script. |

## Diagnostics

`Diagnostics` collects what happens while showing the current page, like the developer tools of a browser. It starts over with every page and is thread-safe:

```cs
await engine.NavigateAsync("https://example.org");

foreach (var request in engine.Diagnostics.Requests)
{
    Console.WriteLine($"{request.Method} {request.Url}: {request.StatusCode} after {request.Duration}");
}

foreach (var message in engine.Diagnostics.Messages)
{
    Console.WriteLine($"[{message.Kind}] {message.Text}");
}
```

The `Changed` event tells when there is something new. `Clear()` removes what has been collected so far.
