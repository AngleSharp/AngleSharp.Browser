namespace AngleSharp.Browser.Pages;

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Avalonia.Threading;

/// <summary>
/// A part of the browser's own UI written in HTML and CSS: it is shown by a
/// <see cref="Controls.BrowserView"/> like any page, but runs no scripts. The page's
/// behavior is .NET code instead, which subscribes to DOM events once the page has loaded.
/// </summary>
public abstract class HtmlPage : IDisposable
{
    private readonly string _name;

    protected HtmlPage(string name, string? colorScheme)
    {
        _name = name;
        Engine = new BrowserEngine(new BrowserOptions
        {
            // the browser's own pages keep no data, and their logic is C#
            ProfileDirectory = null,
            IsScriptingEnabled = false,
            ColorScheme = colorScheme,
        });
    }

    /// <summary>
    /// Gets the engine showing the page.
    /// </summary>
    public BrowserEngine Engine { get; }

    /// <summary>
    /// Loads the page and wires it up.
    /// </summary>
    public async Task LoadAsync()
    {
        var html = ReadResource($"{_name}.html").Replace("{{styles}}", ReadResource("pages.css"));
        await Engine.LoadHtmlAsync(Prepare(html), $"about:{_name}");
        await Engine.InvokeAsync(document =>
        {
            if (document is not null)
            {
                Wire(document);
            }
        });
    }

    public virtual void Dispose() => Engine.Dispose();

    /// <summary>
    /// Fills in placeholders of the page's source before it is loaded.
    /// </summary>
    protected virtual string Prepare(string html) => html;

    /// <summary>
    /// Subscribes to the events of the loaded page. Runs on the engine's event loop, like the
    /// event handlers later on.
    /// </summary>
    protected abstract void Wire(IDocument document);

    /// <summary>
    /// Runs the handler (on the event loop) when the element with the given id is clicked.
    /// </summary>
    protected static void OnClick(IDocument document, string id, Action handler) =>
        document.GetElementById(id)?.AddEventListener("click", (_, _) => handler());

    /// <summary>
    /// Runs the handler on the UI thread, e.g., from an event handler of the page.
    /// </summary>
    protected static void OnUiThread(Action handler) => Dispatcher.UIThread.Post(handler);

    protected static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    protected static string GetValue(IDocument document, string id) => document.GetElementById(id) switch
    {
        IHtmlInputElement input => input.Value,
        IHtmlTextAreaElement area => area.Value,
        _ => string.Empty,
    };

    /// <summary>
    /// Sets the value of a text field. The renderer paints controls from their markup, so the
    /// attribute (or text of a textarea) changes along with the live value.
    /// </summary>
    protected static void SetValue(IDocument document, string id, string value)
    {
        switch (document.GetElementById(id))
        {
            case IHtmlInputElement input:
                input.SetAttribute("value", value);
                input.Value = value;
                break;
            case IHtmlTextAreaElement area:
                area.TextContent = value;
                area.Value = value;
                break;
        }
    }

    protected static bool IsChecked(IDocument document, string id) =>
        document.GetElementById(id) is IHtmlInputElement { IsChecked: true };

    protected static void SetChecked(IDocument document, string id, bool value)
    {
        if (document.GetElementById(id) is IHtmlInputElement input)
        {
            input.IsChecked = value;

            if (value)
            {
                input.SetAttribute("checked", string.Empty);
            }
            else
            {
                input.RemoveAttribute("checked");
            }
        }
    }

    protected static string ReadResource(string name)
    {
        using var reader = new StreamReader(OpenResource(name));
        return reader.ReadToEnd();
    }

    protected static Stream OpenResource(string name) =>
        typeof(HtmlPage).Assembly.GetManifestResourceStream($"AngleSharp.Browser.Pages.{name}")
            ?? throw new InvalidOperationException($"The page resource {name} is missing.");
}
