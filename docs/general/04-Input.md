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

## Selects

A `select` showing several options (`multiple` or `size`) is a list box:

- A click selects a row. With `multiple`, Ctrl (or Cmd) toggles it and Shift selects the rows from the last one clicked.
- The up and down keys move the selection, with Shift (and `multiple`) extending it.
- The wheel scrolls it before the page.

Any other `select` is a dropdown. Pressing it, or Enter, Space or Alt+Down while it has the focus, opens its list:

- `IsSelectPopupOpen` tells if the list is open. Frames from `PrepareFrameAsync` show it on top of the page (the image of `PaintAsync` does not), so a host has nothing to do for it. It stays within the viewport, opening above the select without room below.
- While it is open, the list takes all keys: they move its highlight (skipping what is disabled), Enter, Space or Tab choose the highlighted row, and Escape closes it without a change. A click on a row chooses it.
- A press elsewhere or scrolling the page closes it. So do the select losing the focus or going away, and a change of the viewport.
- While it is closed, the up and down keys, Home, End, Page Up, Page Down and typing change the value right away.
- The list uses the color scheme of the select: dark if its `color-scheme` (or else the `color-scheme` meta element of the page) supports dark, and doesn't also support light unless `ColorScheme` is `dark`. `ColorScheme` alone doesn't make it dark, as in other browsers. Colors of the select and of its options win over those of the scheme.

Choosing a row fires `input` and `change` if the value changed, then `click`.

## Scrolling

`ScrollAsync` scrolls by a distance: what is below the pointer, if that is a scroll container, or else the page. The returned task completes once the scrolling arrived.

```cs
await engine.MovePointerAsync(400, 300);
await engine.ScrollAsync(0, 480);
Console.WriteLine(engine.ScrollPosition);
```
