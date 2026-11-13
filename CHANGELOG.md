# 0.2.0

Released on Friday, November 13 2026.

- Fixed `BrowserView` not passing a modifier key pressed on its own (Control, Shift, Alt, Meta) to the page, e.g. a game firing on Control
- Fixed callers of the engine (e.g. awaiting `PrepareFrameAsync` and rasterizing the frame without a synchronization context) continuing on the event loop, which held up the scripts of the page
- Fixed the pointer not firing `mouseenter`/`mouseleave` (and `pointerenter`/`pointerleave`) and its events having no `relatedTarget`
- Fixed the pointer events of the mouse having no `pointerType`, so that scripts took the mouse for touch (e.g. SmartMenus not opening menus on hover)
- Added `BrowserOptions.SchemeHandlers` for custom URL schemes answered by the host
- Added suggestions of previously visited addresses to the address bar of `Vision`, the most popular first
- Added the `vision:` scheme to `Vision`, showing its own pages (e.g., `vision:settings`) or a list of them

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
