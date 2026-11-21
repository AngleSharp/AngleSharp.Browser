namespace Vision.Pages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AngleSharp.Dom;
using Vision.Models;

/// <summary>
/// The start page (vision:start): the pages visited most, a few demos to try and the other
/// pages of Vision. All of it is plain links, filled in before the page is loaded.
/// </summary>
public sealed class StartPage(string? colorScheme, IReadOnlyList<UrlSuggestion> mostVisited) : HtmlPage("start", colorScheme)
{
    /// <summary>
    /// The most visited pages shown.
    /// </summary>
    public const int MostVisitedCount = 6;

    /// <summary>
    /// The demos to try: address, title and what they show.
    /// </summary>
    public static IReadOnlyList<(string Url, string Title, string Description)> Demos { get; } =
    [
        ("https://demo-full.piral.io", "Piral", "A micro frontend app with pilets loaded at runtime."),
        ("https://picard-sspa-module-federation-tractor-v2.florian-rappl.de", "Tractor Store", "Micro frontends of single-spa and module federation, wired up by Picard.js."),
        ("https://en.wikipedia.org/wiki/CSS", "Wikipedia: CSS", "A long article with tables, images and references."),
        ("https://news.ycombinator.com", "Hacker News", "A classic table layout with plenty of links."),
        ("https://anglesharp-tests.anglevisions.com/renderer/", "Renderer Tests", "A collection of HTML5 rendering tests."),
        ("https://html5test.com", "HTML5test", "How many of the features of HTML5 the browser supports."),
    ];

    /// <summary>
    /// The names of the pages of Vision shown.
    /// </summary>
    private static readonly string[] Pages = ["requester", "settings", "about"];

    /// <summary>
    /// The gradients of the letters (white text on them is readable in both themes).
    /// </summary>
    private static readonly (string From, string To)[] Colors =
    [
        ("#4285f4", "#1a56c4"),
        ("#f06b5c", "#c5221f"),
        ("#34a853", "#146c2e"),
        ("#f5a623", "#c25e00"),
        ("#a46ef5", "#7128d6"),
        ("#26a69a", "#00695c"),
        ("#ec6fa8", "#b8306f"),
        ("#5c6bc0", "#303f9f"),
    ];

    protected override string Prepare(string html)
    {
        var visited = mostVisited.Count > 0
            ? string.Concat(mostVisited.Select(m => Tile(m.Url, m.HasTitle ? m.Title : GetDisplayAddress(m.Url), GetVisits(m.Visits))))
            : """<div class="empty">The pages you visit most will show up here.</div>""";
        var demos = string.Concat(Demos.Select(m => Tile(m.Url, m.Title, m.Description)));
        var pages = string.Concat(VisionPages.Available
            .Where(m => Pages.Contains(m.Name))
            .OrderBy(m => Array.IndexOf(Pages, m.Name))
            .Select(m => Tile(m.Address, m.Title, m.Description)));

        using var logo = new MemoryStream();
        OpenResource("logo.svg").CopyTo(logo);

        return html
            .Replace("{{logo}}", $"data:image/svg+xml;base64,{Convert.ToBase64String(logo.ToArray())}")
            .Replace("{{most-visited}}", visited)
            .Replace("{{demos}}", demos)
            .Replace("{{pages}}", pages);
    }

    protected override void Wire(IDocument document)
    {
    }

    private static string Tile(string url, string title, string description)
    {
        var html = new StringBuilder();
        html.Append($"""<a class="tile" href="{Encode(url)}" title="{Encode(url)}">""");
        html.Append($"""<span class="letter" style="{GetBackground(url)}">{Encode(GetLetter(title))}</span>""");
        html.Append($"""<span class="text"><span class="name">{Encode(title)}</span>""");
        html.Append($"""<span class="address">{Encode(GetDisplayAddress(url))}</span>""");
        html.Append($"""<span class="description">{Encode(description)}</span></span></a>""");
        return html.ToString();
    }

    private static string GetVisits(int visits) => visits == 1 ? "Visited once" : $"Visited {visits} times";

    /// <summary>
    /// The address without what is the same for most of them (the scheme, "www.", a final slash).
    /// </summary>
    private static string GetDisplayAddress(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return $"{host}{uri.PathAndQuery}".TrimEnd('/');
        }

        return url;
    }

    /// <summary>
    /// The letter standing in for the page's icon: the first one of its title (or address).
    /// </summary>
    private static string GetLetter(string title)
    {
        var letter = title.FirstOrDefault(char.IsLetterOrDigit);
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }

    /// <summary>
    /// The background of the letter, which stays the same for the address (by its host).
    /// </summary>
    private static string GetBackground(string url)
    {
        var key = GetDisplayAddress(url).Split('/')[0];
        var hash = 17;

        foreach (var character in key)
        {
            hash = unchecked(hash * 31 + character);
        }

        var (from, to) = Colors[(hash & 0x7fffffff) % Colors.Length];
        return $"background: {to}; background: linear-gradient(135deg, {from} 0%, {to} 100%)";
    }
}
