# AGENTS.md

Guidance for AI coding agents working in this repository. Keep this file as the quick-start reference for the repo's commands, architecture, conventions, and test workflow.

## What This Repository Is

AngleSharp.Browser adds a fully usable (visible) browser based on top of the AngleSharp ecosystem.

## Architecture

- `src/AngleSharp.Browser` (NuGet package): the UI-agnostic `BrowserEngine` (navigation, painting, input). Everything configurable lives in `BrowserOptions`. All DOM work is serialized and runs on AngleSharp's event loop, as AngleSharp is not thread-safe.
- Frames: `BrowserEngine.PrepareFrameAsync` lays out on the event loop (the first frame of a document only what is in view, `PrepareViewportFrame`, the next one the whole page); `BrowserFrame.Rasterize` (raw RGBA, overscan above/below the viewport) runs on any thread, so `BrowserView` pipelines layout and raster. Each frame's hit-test map answers cursor/link lookups without the DOM; scroll-only frames reuse the last display list unless the page has fixed/sticky boxes.
- Page scrolling is smooth and runs ahead of the DOM (`ScrollPosition` vs `ScrollTop`); the view shifts the last frame meanwhile and draws an overlay scrollbar. While a script keeps the page busy, `PrepareFrameAsync` repositions the last frame (DOM-free, like a compositor) instead of waiting for the event loop. A frame after document mutations only (not resources, time or hover: `_needsFullLayout`) asks `IDomHarness.UpdateFrame()` first, which keeps or patches the last frame when the changes are confined to boxes that can be laid out on their own (out-of-flow boxes, flex and grid items, or in-flow blocks staying the same from outside). Until a page has a measured extent, scrolling is not clamped to it (the next frame clamps). A frame that animates asks for the next one as a mutation (`TryReuseLastFrame` advances the clock first), so the harness paints animated transforms/opacity/filters anew without a layout; frames with sticky boxes are repositioned with the boxes stuck anew (Renderer `StickyFrame`). A root with `overflow: hidden` can't be scrolled by the user, but keeps its position.
- Text selection is opt-in (`BrowserOptions.IsTextSelectionEnabled`, Vision turns it on): the engine forwards presses (after an uncanceled `mousedown`), drags and Ctrl+A to the harness of the document pressed in (`BrowserEngine.Selection.cs`), which paints the selection without a layout; the I-beam over text comes from `RenderedFrame.HasTextAt` (DOM-free). Vision's own panels select too (controls have `user-select: none`). Animated images (GIFs) pick their frame when rasterized; `ScheduleImageFrame` asks for a frame once one in view changes, which reuses the last frame (`RenderedFrame.GetTimeToNextImageFrame`).
- Windows: one `BrowserEngine` (browsing context, history) per window. `WindowRequested` (target=_blank, Ctrl+click) lets the host open one via `CreateWindow()`, which shares the profile data (one synchronized cookie provider, storage and cache factories) but gets its own HTTP handler; unhandled, the page navigates in place.
- Iframes: the document of each one has its own browsing context and harness (with a viewport of the iframe's size); the page's layout leaves a `DrawSubframeCommand`, and the nested frame is prepared right after it, so layouts don't nest. The page's hit-test map delegates to the nested one, and `BrowserEngine.Subframes.cs` routes pointer coordinates, hover, focus, wheel and links. All contexts of an engine share one event loop (`FrameEventLoop` scopes cancelling), as the DOM isn't thread-safe. Script isolation is enforced at the boundary in AngleSharp.Js (`OriginPolicy`, `CrossOriginWindow`): C# code here, like the renderer, sees every document, so anything acting for a script in a frame has to act in that frame's document (see `RunScript`, `FollowHyperlink`).
- `src/AngleSharp.Browser.Avalonia` (NuGet package): `BrowserView` displays frames and forwards input; with just a `Source` it creates and owns an engine while attached. Inside of this namespace `Avalonia` means the namespace itself, so `using` directives for the Avalonia framework stay above the namespace declaration.
- Audio: `MediaHost` (one per engine) registers `AudioResourceService` as AngleSharp's `IResourceService<IAudioInfo>`; it reads the whole response, picks an `IAudioCodec` by the data's first bytes (`AudioCodecs`: WAV, NLayer, NVorbis, Concentus) and gives the element an `AudioPlayback` (its `IMediaController`). A playing one opens an `IAudioStream` of `BrowserOptions.AudioOutput` (`SilentAudioOutput` by default), which pulls the samples, so the media's clock is what was pulled; it raises its events on the event loop, and AngleSharp's element fires those of its own state (play, pause, volumechange). Audible media needs a user activation (a press or a key, reset per document) unless `IsAutoplayAllowed`; `SyncActiveDocument` stops the media of documents that were left. The Renderer paints `audio[controls]` (its time and timeline as a command painted when rasterized, so `GetTimeToNextImageFrame` also covers playing media); `BrowserEngine.Media.cs` acts on presses on them (hit tested by the Renderer's `GetMediaControlAt`), and `MediaHost.Changed` lays out a frame when a media changes otherwise (play, pause, seek, volume), but only while a document shows `audio[controls]` (`SyncMediaChanges`): a page playing sounds without them (e.g. a game) keeps its frame. Vision's `Audio/OpenAlAudioOutput` (Silk.NET.OpenAL, one device for all windows) is where the audio is heard.
- Site information: `BrowserEngine.Site.cs` finds the page's icon while syncing the document and records the certificate of each host in the HTTP handler's validation callback (unless the host set its own), keeping the platform's verdict.
- `src/Vision`: the sample app (Avalonia), not packaged. Its icon lives in `Assets/` (`vision.ico` for Windows/Linux, `vision.png` with the macOS margins, `vision.icns`); on macOS a window's icon is the Dock icon, so the page's icon only shows in the address bar there, while other platforms also show it in the title bar. `App` hosts the windows and the user's `Models/BrowserSettings` (JSON in the app data directory), which apply to windows opened after saving.
- Vision's address bar suggests the addresses of `Models/UrlHistory` (`history.json` in the profile directory, in memory for in-memory profiles, shared by the windows of a profile): the most visited first, equally visited ones by their similarity to the typed text (Levenshtein). Its own pages are also served at `vision:` addresses (`Pages/VisionPages`, registered via `BrowserOptions.SchemeHandlers` per window): the window's engine loads the page's source and the `HtmlPage` is attached to its document afterwards; an unknown name shows the list of pages (`VisionPages.Available`, add new pages there). `vision:start` (the default start page) shows the most visited addresses of the window's `UrlHistory`; `vision:requester` sends requests through the engine's `IRequester` services directly (not the loader), so it adds the cookies itself; its saved requests are `Models/SavedRequests` (`requests.json` in the profile directory, shared by the windows of a profile like the history). In tests, `DoClick` dispatches after the current `InvokeAsync` callback, so wait between clicks that depend on each other.
- Vision's own UI beyond the toolbar (diagnostics tab, settings, about) is HTML/CSS in `Pages/` (embedded resources), shown by a `BrowserView` with a scripting-free engine. `HtmlPage` subclasses wire the DOM with .NET event handlers via `BrowserEngine.InvokeAsync`; don't add JavaScript there. `BrowserEngine.Diagnostics` collects the current page's requests, errors, console output and frame timings. The side panel of a window shows one page at a time, the diagnostics or the inspector, which share its width (a third of the window, unless the user resized it); `InspectorPage` builds its views on the event loop of the inspected engine and only looks at expanded rows, so its refreshes stay cheap.
- `src/AngleSharp.Browser.Tests`: NUnit tests driving the engine headlessly. `src/AngleSharp.Browser.Avalonia.Tests`: NUnit tests of the control and of Vision's panels using Avalonia's headless platform with real Skia rendering. Don't use `Assert.ThrowsAsync` with the engine (it blocks and hangs); use an awaited try/catch.
- `build/`: the Fallout build, run via `build.sh`/`build.ps1`, `.github/workflows/ci.yml` calls it. The version comes from the top of `CHANGELOG.md`; a push to `main` publishes a release, one to `devel` a pre-release.
- `docs/`: the documentation, published through the doclet in `src/AngleSharp.Browser.Docs`. Keep it in sync with the public API.
- `BrowserEngine` patches a few upstream gaps (User-Agent header, `noscript` style, text documents never completing, timer delays clamped because a zero-delay `setInterval` never yields before AngleSharp 1.9.0, `label.control` not finding wrapped controls, event listener errors reported by an `ErrorEvent` without a type, and `NavigationHistory` reaching the protected `Document.DocumentUri` setter by reflection, as `pushState` has to change the document's address, and `LegacyKeyboardEvent.Code` as it knows the key's code by itself from then on); remove them once fixed upstream.

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
