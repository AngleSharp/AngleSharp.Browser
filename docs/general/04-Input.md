---
title: "Input"
section: "AngleSharp.Browser"
---
# Input

The engine translates input into DOM interactions. It fires the same events as a browser, and performs the default actions of the page, such as following a link, focusing a text field, toggling a checkbox, or submitting a form.

All positions are in CSS pixels relative to the viewport.

## Pointer

| Member | Description |
| --- | --- |
| `MovePointer(x, y)` | Moves the pointer without waiting. Moves are coalesced, which is what a UI should use. |
| `MovePointerAsync(x, y)` | Moves the pointer and waits until the hover state has been updated. |
| `PointerDownAsync(x, y, modifiers)` | Presses the primary button. |
| `PointerUpAsync(x, y, modifiers)` | Releases the primary button. |
| `ClickAsync(x, y, modifiers)` | Presses and releases the primary button. |
| `HoveredLink`, `Cursor` | The link below the pointer and the `BrowserCursor` a host should display. |

Awaiting a click also awaits the navigation it caused:

```cs
await engine.ClickAsync(40, 390);
Console.WriteLine(engine.Url);
```

Holding Control while clicking a link requests a new window, as does a link with `target="_blank"`. See [Multiple Windows](05-Configuration.md#multiple-windows).

```cs
await engine.ClickAsync(40, 390, InputModifiers.Control);
```

## Keyboard

Keys are named by their DOM key value, e.g., `"a"`, `"Enter"`, `"Backspace"`, `"Tab"`, or `"ArrowDown"`. Keys and text are separate on purpose, just like in most UI frameworks:

- `KeyDownAsync` and `KeyUpAsync` fire the keyboard events and perform the actions of keys, e.g., Tab moving the focus, or Enter submitting a form.
- `TextInputAsync` enters text into the focused text field. It works for whole strings, too.

```cs
await engine.ClickAsync(100, 200);
await engine.TextInputAsync("angle brackets");
await engine.KeyDownAsync("Enter");
await engine.KeyUpAsync("Enter");
```

## Scrolling

`ScrollAsync` scrolls by a distance: what is below the pointer, if that is a scroll container, or else the page. The returned task completes once the scrolling arrived.

```cs
await engine.MovePointerAsync(400, 300);
await engine.ScrollAsync(0, 480);
Console.WriteLine(engine.ScrollPosition);
```
