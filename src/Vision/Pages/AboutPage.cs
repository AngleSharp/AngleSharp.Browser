namespace Vision.Pages;

using System;
using AngleSharp.Browser;
using System.IO;
using System.Linq;
using System.Reflection;
using AngleSharp.Dom;

/// <summary>
/// The about dialog: the logo, links and contributors are static HTML, the versions of the
/// components are filled in from the loaded assemblies.
/// </summary>
public sealed class AboutPage(string? colorScheme, Action close) : HtmlPage("about", colorScheme)
{
    private static readonly (string Name, Type Type)[] Components =
    [
        ("AngleSharp", typeof(IDocument)),
        ("AngleSharp.Css", typeof(AngleSharp.Css.ICssDefaultStyleSheetProvider)),
        ("AngleSharp.Js", typeof(AngleSharp.Scripting.JsScriptingService)),
        ("AngleSharp.Io", typeof(AngleSharp.Io.IoOptions)),
        ("AngleSharp.Renderer", typeof(AngleSharp.Renderer.IDomHarness)),
        ("Browser engine", typeof(BrowserEngine)),
    ];

    protected override string Prepare(string html)
    {
        using var logo = new MemoryStream();
        OpenResource("logo.png").CopyTo(logo);
        return html.Replace("{{logo}}", $"data:image/png;base64,{Convert.ToBase64String(logo.ToArray())}");
    }

    protected override void Wire(IDocument document)
    {
        document.GetElementById("version")!.TextContent = $"Version {GetVersion(typeof(AboutPage))}";
        document.GetElementById("components")!.InnerHtml = string.Concat(Components.Select(m => $"""
            <div class="component"><div class="name">{Encode(m.Name)}</div><div class="number">{Encode(GetVersion(m.Type))}</div></div>
            """));
        OnClick(document, "close", () => OnUiThread(close));
    }

    private static string GetVersion(Type type)
    {
        var assembly = type.Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        // drop the commit hash appended by the SDK
        return version.Split('+')[0];
    }
}
