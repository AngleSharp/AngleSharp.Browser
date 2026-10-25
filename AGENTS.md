# AGENTS.md

Guidance for AI coding agents working in this repository. Keep this file as the quick-start reference for the repo's commands, architecture, conventions, and test workflow.

## What This Repository Is

AngleSharp.Browser adds a fully usable (visible) browser based on top of the AngleSharp ecosystem.

## Architecture

- `src/AngleSharp.Browser.Core`: the UI-agnostic `BrowserEngine` (navigation, painting, input). Everything configurable lives in `BrowserOptions`. All DOM work is serialized and runs on AngleSharp's event loop, as AngleSharp is not thread-safe.
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
