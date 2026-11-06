# 0.1.0

Released on Monday, November 2 2026.

- Added `AngleSharp.Browser`, the UI-agnostic `BrowserEngine` for navigating, painting and interacting with pages
- Added `AngleSharp.Browser.Avalonia`, the `BrowserView` control displaying web pages in Avalonia applications
- Added iframes, with their documents isolated by origin and sandbox, and input routed into them
- Added `Vision`, a sample browser built on top of the control
- Added details of errors (type, location and stack trace), request headers and frame timings to `BrowserDiagnostics`
- Added a waterfall of the requests, the rendering timings and the stack traces of errors to the diagnostics of `Vision`
- Added an inspector to `Vision`, showing the DOM, the CSSOM and the render tree of a page
- Added the page's icon (`IconUrl`, `IconUrls`, `LoadIconAsync`) and the certificate of its connection (`Certificate`, `ServerCertificate`) to `BrowserEngine`
- Added the page's icon and a site information window (the certificate and its chain) to `Vision`, which got its own icon and application menu
