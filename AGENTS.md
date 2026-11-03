# AGENTS.md

Guidance for AI coding agents working in this repository. Keep this file as the quick-start reference for the repo's commands, architecture, conventions, and test workflow.

## What This Repository Is

AngleSharp.Browser adds a fully usable (visible) browser based on top of the AngleSharp ecosystem.

## Architecture

- `src/AngleSharp.Browser` (NuGet package): the UI-agnostic `BrowserEngine` (navigation, painting, input). Everything configurable lives in `BrowserOptions`. All DOM work is serialized and runs on AngleSharp's event loop, as AngleSharp is not thread-safe.
- Frames: `BrowserEngine.PrepareFrameAsync` lays out on the event loop; `BrowserFrame.Rasterize` (raw RGBA, overscan above/below the viewport) runs on any thread, so `BrowserView` pipelines layout and raster. Each frame's hit-test map answers cursor/link lookups without the DOM; scroll-only frames reuse the last display list unless the page has fixed/sticky boxes.
- Page scrolling is smooth and runs ahead of the DOM (`ScrollPosition` vs `ScrollTop`); the view shifts the last frame meanwhile and draws an overlay scrollbar. While a script keeps the page busy, `PrepareFrameAsync` repositions the last frame (DOM-free, like a compositor) instead of waiting for the event loop. A frame after document mutations only (not resources, time or hover: `_needsFullLayout`) asks `IDomHarness.UpdateFrame()` first, which keeps or patches the last frame when the changes are confined to boxes that can be laid out on their own (out-of-flow boxes, or in-flow blocks staying the same from outside). Until a page has a measured extent, scrolling is not clamped to it (the next frame clamps).
- Windows: one `BrowserEngine` (browsing context, history) per window. `WindowRequested` (target=_blank, Ctrl+click) lets the host open one via `CreateWindow()`, which shares the profile data (one synchronized cookie provider, storage and cache factories) but gets its own HTTP handler; unhandled, the page navigates in place.
- Iframes: the document of each one has its own browsing context and harness (with a viewport of the iframe's size); the page's layout leaves a `DrawSubframeCommand`, and the nested frame is prepared right after it, so layouts don't nest. The page's hit-test map delegates to the nested one, and `BrowserEngine.Subframes.cs` routes pointer coordinates, hover, focus, wheel and links. All contexts of an engine share one event loop (`FrameEventLoop` scopes cancelling), as the DOM isn't thread-safe. Script isolation is enforced at the boundary in AngleSharp.Js (`OriginPolicy`, `CrossOriginWindow`): C# code here, like the renderer, sees every document, so anything acting for a script in a frame has to act in that frame's document (see `RunScript`, `FollowHyperlink`).
- `src/AngleSharp.Browser.Avalonia` (NuGet package): `BrowserView` displays frames and forwards input; with just a `Source` it creates and owns an engine while attached. Inside of this namespace `Avalonia` means the namespace itself, so `using` directives for the Avalonia framework stay above the namespace declaration.
- `src/Vision`: the sample app (Avalonia), not packaged. `App` hosts the windows and the user's `Models/BrowserSettings` (JSON in the app data directory), which apply to windows opened after saving.
- Vision's own UI beyond the toolbar (diagnostics tab, settings, about) is HTML/CSS in `Pages/` (embedded resources), shown by a `BrowserView` with a scripting-free engine. `HtmlPage` subclasses wire the DOM with .NET event handlers via `BrowserEngine.InvokeAsync`; don't add JavaScript there. `BrowserEngine.Diagnostics` collects the current page's requests, errors and console output.
- `src/AngleSharp.Browser.Tests`: NUnit tests driving the engine headlessly. `src/AngleSharp.Browser.Avalonia.Tests`: NUnit tests of the control using Avalonia's headless platform with real Skia rendering. Don't use `Assert.ThrowsAsync` with the engine (it blocks and hangs); use an awaited try/catch.
- `build/`: the Fallout build, run via `build.sh`/`build.ps1`, `.github/workflows/ci.yml` calls it. The version comes from the top of `CHANGELOG.md`; a push to `main` publishes a release, one to `devel` a pre-release.
- `docs/`: the documentation, published through the doclet in `src/AngleSharp.Browser.Docs`. Keep it in sync with the public API.
- `BrowserEngine` patches a few upstream gaps (User-Agent header, `noscript` style, text documents never completing, timer delays clamped because a zero-delay `setInterval` never yields before AngleSharp 1.9.0, `label.control` not finding wrapped controls); remove them once fixed upstream.

## Most Important Commands

Use the solution-level test command for normal validation:

```bash
dotnet test src/AngleSharp.Browser.sln
```

Run the full build (tests and packages):

```bash
./build.sh -Target Default
```

## Code Conventions

Follow the repository's existing C# style, which is defined by `.editorconfig` and the existing source files:

- 4 spaces for code files, 2 spaces for `.csproj` files.
- LF line endings and UTF-8.
- Trim trailing whitespace.
- Use `var` where the type is obvious from the right-hand side.
- Keep changes narrow and consistent with nearby code.
- Preserve the existing file style instead of reformatting whole files.
