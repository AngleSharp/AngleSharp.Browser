# AGENTS.md

Guidance for AI coding agents working in this repository. Keep this file as the quick-start reference for the repo's commands, architecture, conventions, and test workflow.

## What This Repository Is

AngleSharp.Browser adds a fully usable (visible) browser based on top of the AngleSharp ecosystem.

## Architecture

- `src/AngleSharp.Browser.Core`: the UI-agnostic `BrowserEngine` (navigation, painting, input). Everything configurable lives in `BrowserOptions`. All DOM work is serialized and runs on AngleSharp's event loop, as AngleSharp is not thread-safe.
- Frames: `BrowserEngine.PrepareFrameAsync` lays out on the event loop; `BrowserFrame.Rasterize` (raw RGBA, overscan above/below the viewport) runs on any thread, so `BrowserView` pipelines layout and raster. Each frame's hit-test map answers cursor/link lookups without the DOM; scroll-only frames reuse the last display list unless the page has fixed/sticky boxes.
- Page scrolling is smooth and runs ahead of the DOM (`ScrollPosition` vs `ScrollTop`); the view shifts the last frame meanwhile and draws an overlay scrollbar.
- Windows: one `BrowserEngine` (browsing context, history) per window. `WindowRequested` (target=_blank, Ctrl+click) lets the host open one via `CreateWindow()`, which shares the profile data (one synchronized cookie provider, storage and cache factories) but gets its own HTTP handler; unhandled, the page navigates in place.
- `src/AngleSharp.Browser`: the Avalonia app; `Controls/BrowserView` displays frames and forwards input.
- `src/AngleSharp.Browser.Tests`: NUnit tests driving the engine headlessly.
- `BrowserEngine` patches a few upstream gaps (User-Agent header, `noscript` style, text documents never completing); remove them once fixed upstream.

## Most Important Commands

Use the solution-level test command for normal validation:

```bash
dotnet test src/AngleSharp.Browser.sln
```

## Code Conventions

Follow the repository's existing C# style, which is defined by `.editorconfig` and the existing source files:

- 4 spaces for code files, 2 spaces for `.csproj` files.
- LF line endings and UTF-8.
- Trim trailing whitespace.
- Use `var` where the type is obvious from the right-hand side.
- Keep changes narrow and consistent with nearby code.
- Preserve the existing file style instead of reformatting whole files.
