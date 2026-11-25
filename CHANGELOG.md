# 0.3.0

Released on Tuesday, November 24 2026.

- Fixed Home, End and the arrow keys doing nothing in text fields; they move the cursor (with Shift selecting text), which typing, Backspace and Delete edit at, like in browsers
- Fixed a page loaded with a fragment (e.g. `acid2.html#top`) not being scrolled to the element it names
- Fixed the rendering of the Acid2 test
- Fixed the media queries of an iframe's document not seeing its viewport, also right after a script resized the iframe
- Fixed `(max-monochrome: 0)` and the like not matching
- Fixed a navigated image (e.g. a PNG in an iframe) being parsed as HTML; like in browsers it is shown in a document of its own
- Fixed a navigated XML document that isn't well-formed failing to load (e.g. in an iframe, without a `load` event); like in browsers it shows the error as a `parsererror` element
- Fixed clicking a `select` or `textarea` inside a `label` crashing the browser (endless recursion)
- Fixed the events of the user (e.g. clicks and keys) not being trusted (`isTrusted`)
- Fixed images showing the first candidate of their `srcset` (e.g. pixelated, scaled up); they show the one fitting their `sizes`, the device pixel ratio and the media of a `picture`'s sources, are as large as their density says (e.g. `2x`), and are only loaded once shown (or right away when not in the page, e.g. `new Image()`); `loading=lazy` images load once scrolled near
- Improved the design of Vision's start page, which shows the six most visited pages and HTML5test as a demo
- Updated the default user agent to name the engine (e.g. `Mozilla/5.0 (X11; Linux x86_64) AngleSharp.Browser/0.3`)
- Updated colors to be serialized as CSSOM says, e.g. `getComputedStyle` gives `rgb(0, 128, 0)` instead of `rgba(0, 128, 0, 1)` (`CssColorValue.UseSpecSerialization`, set process-wide)
- Added `:visited` to links to pages shown before (partitioned by the site of the page, so pages can't learn of the history elsewhere)
- Added dragging sliders (`input[type=range]`) and changing them with the keyboard
- Added buttons with `popovertarget` showing, hiding or toggling their popover, and light dismiss of popovers (a press outside or Esc)
- Added paint worklets (`CSS.paintWorklet.addModule()` with `registerPaint`), whose `paint()` images backgrounds show
- Added the Fullscreen API: `requestFullscreen()` (after a press or a key), `document.exitFullscreen()`, `fullscreenElement` and `fullscreenEnabled`, with `fullscreenchange` events and Esc ending it; the engine raises `FullscreenChanged`, and Vision shows the page fullscreen meanwhile
- Added WebAssembly as in browsers (AngleSharp.Wasm with AngleSharp.Js): `WebAssembly.instantiate`/`instantiateStreaming` with import objects of scripts, exports scripts call, and `memory.buffer` sharing the memory; before, `WebAssembly` was undefined
- Added `crypto.subtle` (AngleSharp.Js)

# 0.2.0

Released on Wednesday, November 18 2026.

- Fixed `BrowserView` not passing a modifier key pressed on its own (Control, Shift, Alt, Meta) to the page, e.g. a game firing on Control
- Fixed callers of the engine (e.g. awaiting `PrepareFrameAsync` and rasterizing the frame without a synchronization context) continuing on the event loop, which held up the scripts of the page
- Fixed the pointer not firing `mouseenter`/`mouseleave` (and `pointerenter`/`pointerleave`) and its events having no `relatedTarget`
- Fixed the pointer events of the mouse having no `pointerType`, so that scripts took the mouse for touch (e.g. SmartMenus not opening menus on hover)
- Fixed scripts reading `event` outside of a listener failing, e.g. the carousels of smapiot.com (AngleSharp.Js 1.2.2)
- Fixed pages scrolling a script finds the position of an element on (e.g. smapiot.com) freezing (AngleSharp.Renderer 0.11.4)
- Fixed media changing (e.g. a game playing a sound effect, which rewinds and plays an `audio` element) laying out the whole page, which stalled the frames (e.g. whenever Mario jumps in the Mario 5 demo of demo-full.piral.io); only a page showing `audio controls` is laid out anew
- Improved frames of CSS animations and transitions of the transform, opacity or filter of boxes, and scrolling pages with sticky boxes: the last frame is painted anew instead of the page being laid out (smapiot.com scrolls with about 4 ms instead of 400 ms per frame, AngleSharp.Renderer 0.11.4)
- Added `BrowserOptions.IsTextSelectionEnabled` for selecting the text of pages with the pointer (a double press selects a word, a triple one a paragraph, Shift extends) and Control+A, `PointerDownAsync` with the click count, and `CopySelectionAsync` for the clipboard; `BrowserView` passes the click count and copies with Control+C
- Added animated images (e.g. GIFs), whose frames are shown in turn without a layout of the page
- Added the text cursor over text that can be selected, unless the page asks for another cursor
- Added selecting text to the windows and the panels of `Vision`
- Added `BrowserOptions.SchemeHandlers` for custom URL schemes answered by the host
- Added audio: `audio` elements and `new Audio()` play WAV, MP3, Ogg Vorbis and Ogg Opus (managed decoders, `BrowserOptions.AudioCodecs`) on the host's `BrowserOptions.AudioOutput` (silently without one), audible media once the user interacted with the page (`BrowserOptions.IsAutoplayAllowed`), and stop once their document is left (AngleSharp 1.10.4, AngleSharp.Js 1.2.3); `audio controls` shows a player (play/pause, time, timeline, mute) that the pointer operates, and whose time goes on without a layout of the page (AngleSharp.Renderer 0.11.4); Vision plays the audio on the default device via OpenAL Soft
- Added suggestions of previously visited addresses to the address bar of `Vision`, the most popular first
- Added the `vision:` scheme to `Vision`, showing its own pages (e.g., `vision:settings`) or a list of them
- Added the start page `vision:start` to `Vision` (its new default start page), with the most visited pages, demos and Vision's own pages
- Added `BrowserEngine.History` (the entries with their titles) and `BrowserEngine.GoAsync` for jumping several entries; holding the back or forward button of `Vision` (or right-clicking it) shows the entries to jump to
- Added the requester `vision:requester` to `Vision`, which sends HTTP requests with the requesters of the window and shows their responses, and keeps requests saved under a name per profile (saved from a dialog, listed in a sheet opened from the top-left corner)

# 0.1.0

Released on Monday, November 9 2026.

- Added `AngleSharp.Browser`, the UI-agnostic `BrowserEngine` for navigating, painting and interacting with pages
- Added `AngleSharp.Browser.Avalonia`, the `BrowserView` control displaying web pages in Avalonia applications
- Added iframes, with their documents isolated by origin and sandbox, and input routed into them
- Added `Vision`, a sample browser built on top of the control
- Added details of errors (type, location and stack trace), request headers and frame timings to `BrowserDiagnostics`
- Added a waterfall of the requests, the rendering timings and the stack traces of errors to the diagnostics of `Vision`
- Added an inspector to `Vision`, showing the DOM, the CSSOM and the render tree of a page
- Added the server GC to `Vision`, which shortens the pauses for garbage collection while laying out large pages
- Added precompiled code (ReadyToRun) to published versions of `Vision`, and optimizing often called code without a delay, which shortens the first frames of a page
- Added the metrics of the renderer to the frame timings of `BrowserDiagnostics` (`FrameTiming.Metrics`), and their breakdown to the performance diagnostics of `Vision`
- Added showing a page that was just loaded early on: its first frame styles and lays out just what is in view, the next one all of the page
- Added patching the frame after hovering or focusing an element, which is painted again instead of laid out when only colors, backgrounds, borders or text decoration change
- Added the page's icon (`IconUrl`, `IconUrls`, `LoadIconAsync`) and the certificate of its connection (`Certificate`, `ServerCertificate`) to `BrowserEngine`
- Added the page's icon and a site information window (the certificate and its chain) to `Vision`, which got its own icon and application menu
- Added decompressing responses (gzip, deflate, brotli), which some servers send without being asked (e.g. the CDN of the Microsoft login)
- Added `navigator.serviceWorker`: pages can register service workers, which are installed and activated, but don't run (requests go to the network)
