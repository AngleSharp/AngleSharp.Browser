namespace AngleSharp.Browser;

using AngleSharp.Attributes;
using AngleSharp.Dom;
using AngleSharp.Html.Dom.Events;
using System.Linq;

/// <summary>
/// A keyboard event that also carries the legacy key codes (UI Events, Legacy key models), which
/// many scripts still use, e.g. <c>e.keyCode === 13</c> for Enter.
/// </summary>
/// <remarks>
/// The own DOM name gives it a prototype of its own in scripts; under the inherited name of
/// <see cref="KeyboardEvent"/> its members would not be visible.
/// </remarks>
[DomName("LegacyKeyboardEvent")]
public sealed class LegacyKeyboardEvent : KeyboardEvent
{
    /// <summary>
    /// Creates a new keyboard event for the given key.
    /// </summary>
    /// <param name="type">The type, e.g. keydown.</param>
    /// <param name="view">The window of the document.</param>
    /// <param name="key">The DOM key value.</param>
    /// <param name="modifiersList">The held modifiers, separated by spaces.</param>
    public LegacyKeyboardEvent(string type, IWindow? view, string key, string modifiersList)
        : base(type, bubbles: true, cancelable: true, view: view, key: key, modifiersList: modifiersList)
    {
        var charCode = type == "keypress" ? GetCharCode(key) : 0;
        KeyCode = type == "keypress" ? charCode : GetKeyCode(key);
        CharCode = charCode;
    }

    /// <summary>
    /// Gets the physical key, as far as the key value tells: a host only knows what was typed.
    /// </summary>
    [DomName("code")]
    public string Code => GetCode(Key ?? string.Empty);

    /// <summary>
    /// Gets the legacy key code, e.g. 13 for Enter (the character code for keypress).
    /// </summary>
    [DomName("keyCode")]
    public int KeyCode { get; }

    /// <summary>
    /// Gets the character code of a keypress, 0 for other events.
    /// </summary>
    [DomName("charCode")]
    public int CharCode { get; }

    /// <summary>
    /// Gets the legacy key code, like <see cref="KeyCode"/>.
    /// </summary>
    [DomName("which")]
    public int Which => KeyCode;

    /// <summary>
    /// Gets if a keypress is fired for the key, i.e., it produces a character (or is Enter).
    /// </summary>
    public static bool ProducesCharacter(string key) => GetCharCode(key) != 0;

    private static int GetCharCode(string key) => key switch
    {
        "Enter" => 13,
        { Length: 1 } => key[0],
        _ => 0,
    };

    // the virtual key codes of a US keyboard layout, which browsers report
    private static int GetKeyCode(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetter(key[0]) => char.ToUpperInvariant(key[0]),
        { Length: 1 } when char.IsAsciiDigit(key[0]) => key[0],
        " " => 32,
        "Backspace" => 8,
        "Tab" => 9,
        "Enter" => 13,
        "Shift" => 16,
        "Control" => 17,
        "Alt" => 18,
        "Escape" => 27,
        "PageUp" => 33,
        "PageDown" => 34,
        "End" => 35,
        "Home" => 36,
        "ArrowLeft" => 37,
        "ArrowUp" => 38,
        "ArrowRight" => 39,
        "ArrowDown" => 40,
        "Insert" => 45,
        "Delete" => 46,
        "Meta" => 91,
        "," => 188,
        "-" => 189,
        "." => 190,
        "/" => 191,
        _ when key.Length > 1 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 12 => 111 + number,
        _ => 0,
    };

    private static string GetCode(string key) => key switch
    {
        " " => "Space",
        "Enter" or "Tab" or "Escape" or "Backspace" or "Delete" or "Home" or "End" or "PageUp" or "PageDown" or "Insert"
            or "ArrowUp" or "ArrowDown" or "ArrowLeft" or "ArrowRight" => key,
        // the view can't tell the left modifier from the right one
        "Shift" or "Control" or "Alt" or "Meta" => key + "Left",
        { Length: 1 } when char.IsAsciiLetter(key[0]) => "Key" + char.ToUpperInvariant(key[0]),
        { Length: 1 } when char.IsAsciiDigit(key[0]) => "Digit" + key,
        _ => key.Length > 1 && key[0] == 'F' && key[1..].All(char.IsDigit) ? key : string.Empty,
    };
}
