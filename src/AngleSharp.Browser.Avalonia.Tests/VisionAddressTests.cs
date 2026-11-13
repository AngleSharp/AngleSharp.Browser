using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using Vision.Models;
using Vision.Pages;
using Vision.ViewModels;

namespace AngleSharp.Browser.Avalonia.Tests;

/// <summary>
/// The address bar of Vision: its suggestions and the browser's own pages (vision:).
/// </summary>
[TestFixture]
public class VisionAddressTests
{
    [Test]
    public void SuggestionsAreSortedByPopularityThenBySimilarity()
    {
        var history = new UrlHistory();
        Visit(history, "https://github.com/AngleSharp/AngleSharp", 1);
        Visit(history, "https://github.com/", 3);
        Visit(history, "https://gist.github.com/", 3);
        Visit(history, "https://example.org/", 9);

        var suggestions = history.Suggest("github.com").Select(m => m.Url).ToArray();

        // equally popular: the one closer to what was typed comes first
        Assert.That(suggestions, Is.EqualTo(new[] { "https://github.com/", "https://gist.github.com/", "https://github.com/AngleSharp/AngleSharp" }));
    }

    [Test]
    public void SuggestionsMatchTitlesAndLeaveOutBlankPages()
    {
        var history = new UrlHistory();
        history.Record("https://anglesharp.github.io/#docs", "AngleSharp Documentation");
        history.Record("about:blank");
        history.Record("data:text/html,documentation");

        var suggestions = history.Suggest("documentation");

        Assert.That(suggestions.Select(m => m.Url), Is.EqualTo(new[] { "https://anglesharp.github.io/" }));
        Assert.That(suggestions[0].Heading, Is.EqualTo("AngleSharp Documentation"));
        Assert.That(history.Suggest("   "), Is.Empty);
    }

    [Test]
    public void TheHistoryIsKeptInItsDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("vision-history-").FullName;

        try
        {
            var history = new UrlHistory(directory);
            Visit(history, "https://example.org/", 2);
            history.SetTitle("https://example.org/", "Example");

            var loaded = new UrlHistory(directory).Suggest("example");

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0], Is.EqualTo(new UrlSuggestion("https://example.org/", "Example", 2)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void SimilarityGoesFromNothingToTheSame()
    {
        Assert.That(UrlHistory.GetSimilarity("github.com", "github.com"), Is.EqualTo(1));
        Assert.That(UrlHistory.GetSimilarity("GitHub.com", "github.com"), Is.EqualTo(1));
        Assert.That(UrlHistory.GetSimilarity("abc", "xyz"), Is.EqualTo(0));
        Assert.That(UrlHistory.GetSimilarity("github", "github.com"), Is.GreaterThan(UrlHistory.GetSimilarity("github", "github.com/AngleSharp")));
    }

    [AvaloniaTest]
    public async Task TheArrowKeysChooseTheSuggestionToNavigateTo()
    {
        var history = new UrlHistory();
        Visit(history, "vision:about", 2);
        Visit(history, "vision:settings", 1);
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);
        using var viewModel = new MainViewModel(engine, "about:blank", host, history);

        viewModel.Address = "vision";

        Assert.That(viewModel.IsSuggesting, Is.True);
        Assert.That(viewModel.Suggestions.Select(m => m.Url), Is.EqualTo(new[] { "vision:about", "vision:settings" }));
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(-1));

        // down to the second one, up and around back to it
        Assert.That(viewModel.MoveSuggestion(1), Is.True);
        Assert.That(viewModel.MoveSuggestion(1), Is.True);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(1));
        viewModel.MoveSuggestion(1);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(-1));
        viewModel.MoveSuggestion(-1);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(1));

        await viewModel.NavigateCommand.ExecuteAsync(null);

        Assert.That(viewModel.IsSuggesting, Is.False);
        Assert.That(viewModel.Address, Is.EqualTo("vision:settings"));
        Assert.That(engine.Url, Is.EqualTo("vision:settings"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task WithoutAChosenSuggestionTheTypedAddressIsUsed()
    {
        var history = new UrlHistory();
        Visit(history, "vision:about", 1);
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);
        using var viewModel = new MainViewModel(engine, "about:blank", host, history);

        viewModel.Address = "vision:pages";
        Assert.That(viewModel.IsSuggesting, Is.False);
        viewModel.Address = "vision:";
        Assert.That(viewModel.IsSuggesting, Is.True);
        Assert.That(viewModel.CloseSuggestions(), Is.True);
        Assert.That(viewModel.MoveSuggestion(1), Is.False);

        await viewModel.NavigateCommand.ExecuteAsync(null);

        Assert.That(engine.Url, Is.EqualTo("vision:"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheSettingsAreAVisionPage()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:settings");

        await WaitUntil(engine, d => d?.GetElementById("start-page") is IHtmlInputElement { Value: BrowserSettings.DefaultStartPage });
        Assert.That(engine.Title, Is.EqualTo("Settings"));

        // the page is wired up: the button works
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("reset")!).DoClick());
        await WaitUntil(engine, d => d!.GetElementById("status")!.TextContent.Contains("Defaults restored"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task AnUnknownVisionPageListsThePagesThereAre()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:nothing");

        var (notice, links) = await engine.InvokeAsync(d => (
            d!.GetElementById("notice")?.TextContent,
            d.QuerySelectorAll("#pages a").Select(m => m.GetAttribute("href")).ToArray()));
        Assert.That(notice, Does.Contain("vision:nothing"));
        Assert.That(links, Is.EqualTo(VisionPages.Available.Select(m => m.Address).ToArray()));

        await engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector("#pages a[href='vision:about']")!).DoClick());
        await WaitUntil(engine, d => d?.Url == "vision:about" && d.GetElementById("version")!.TextContent.StartsWith("Version"));

        await engine.NavigateAsync("vision:pages");
        Assert.That(await engine.InvokeAsync(d => d!.GetElementById("notice")), Is.Null);
        pages.Dispose();
    }

    [TestCase("vision:settings", "settings")]
    [TestCase("vision://About/", "about")]
    [TestCase("vision:settings?x=1#y", "settings")]
    [TestCase("vision:", "")]
    public void TheNameOfAVisionPageComesFromItsAddress(string url, string name)
    {
        Assert.That(VisionPages.GetName(url), Is.EqualTo(name));
    }

    private static void Visit(UrlHistory history, string url, int times)
    {
        for (var i = 0; i < times; i++)
        {
            history.Record(url);
        }
    }

    private static BrowserEngine CreateEngine(IBrowserHost host, out VisionPages pages)
    {
        var options = new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 };
        pages = new VisionPages(host);
        options.SchemeHandlers[VisionPages.Scheme] = pages.HandleAsync;
        var engine = new BrowserEngine(options);
        pages.Attach(engine);
        return engine;
    }

    private static async Task WaitUntil(BrowserEngine engine, Func<IDocument?, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!await engine.InvokeAsync(condition))
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "Condition was not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private sealed class TestHost : IBrowserHost
    {
        // like the application, whose theme is an Avalonia property
        public string ColorScheme
        {
            get
            {
                Dispatcher.UIThread.VerifyAccess();
                return "light";
            }
        }

        public string StartPage => "about:blank";

        public event EventHandler? ColorSchemeChanged
        {
            add { }
            remove { }
        }

        public HtmlPage CreateSettingsPage(Action close) => new SettingsPage(ColorScheme, new BrowserSettings(), _ => null, close);

        public void OpenWindow(string? address, BrowserEngine? opener)
        {
        }

        public void OpenSettings()
        {
        }

        public void OpenAbout(BrowserEngine owner)
        {
        }

        public void OpenSiteInfo(BrowserEngine owner)
        {
        }
    }
}
