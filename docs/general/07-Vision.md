---
title: "Vision Sample"
section: "AngleSharp.Browser"
---
# Vision Sample

Vision is the sample application living in `src/Vision`: a small desktop browser built on `AngleSharp.Browser.Avalonia`. It is a proof of concept, which shows how the packages fit together, and is not shipped as a package.

## Running

```sh
dotnet run --project src/Vision -- https://example.org
```

| Argument | Description |
| --- | --- |
| `<address>` | The address to open instead of the start page. |
| `--profile <directory>` | The directory to use for cookies, web storage and the HTTP cache. |
| `--in-memory` | Persists no browsing data. |

The profile and the settings are kept in the `Vision` folder of the local application data directory by default.

## What to Look At

| File | Shows |
| --- | --- |
| `App.axaml.cs` | One engine per window, sharing a profile using `CreateWindow`, and handling `WindowRequested`. |
| `ViewModels/MainViewModel.cs` | A toolbar and status bar on top of the engine's state and events. |
| `Views/MainWindow.axaml` | Placing `BrowserView` in a window, next to a second one for the diagnostics. |
| `Models/BrowserSettings.cs` | Turning user settings into `BrowserOptions`. |
| `Pages/` | The app's own UI (diagnostics, settings, about) written as HTML and CSS, and wired up with .NET event handlers through `InvokeAsync`, in an engine with scripting disabled. |
