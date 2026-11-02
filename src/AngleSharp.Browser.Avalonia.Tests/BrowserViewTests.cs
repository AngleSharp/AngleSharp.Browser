using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using NUnit.Framework;

namespace AngleSharp.Browser.Avalonia.Tests;

[TestFixture]
public class BrowserViewTests
{
    private static readonly string RedPage = CreatePage("Red", "rgb(255,0,0)");
    private static readonly string BluePage = CreatePage("Blue", "rgb(0,0,255)");

    [AvaloniaTest]
    public async Task SettingTheSourceCreatesAnEngineAndNavigates()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);

        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine!.Url, Is.EqualTo(RedPage));
        window.Close();
    }

    [AvaloniaTest]
    public void WithoutASourceNoEngineIsCreated()
    {
        var view = new BrowserView();
        var window = Show(view);

        Assert.That(view.Engine, Is.Null);
        window.Close();
    }

    [AvaloniaTest]
    public async Task ChangingTheSourceNavigatesAgain()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var engine = view.Engine;

        view.Source = BluePage;
        await WaitUntil(() => view.Engine?.Title == "Blue");

        Assert.That(view.Engine, Is.SameAs(engine));
        Assert.That(engine!.CanGoBack, Is.True);
        window.Close();
    }

    [AvaloniaTest]
    public async Task TheSourceFollowsNavigationsOfTheEngine()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");

        await view.Engine!.NavigateAsync(BluePage);
        await WaitUntil(() => view.Source == BluePage);

        Assert.That(view.Engine.Title, Is.EqualTo("Blue"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineCreatedByTheViewGoesAwayWhenDetachedAndComesBackWhenAttached()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var engine = view.Engine!;

        window.Content = null;

        Assert.That(view.Engine, Is.Null);
        Assert.That(await IsDisposedAsync(engine), Is.True);

        window.Content = view;
        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine, Is.Not.SameAs(engine));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineOfTheHostIsKeptWhenDetached()
    {
        using var engine = new BrowserEngine();
        var view = new BrowserView { Engine = engine };
        var window = Show(view);

        window.Content = null;
        await engine.LoadHtmlAsync("<title>Alive</title>");

        Assert.That(view.Engine, Is.SameAs(engine));
        Assert.That(engine.Title, Is.EqualTo("Alive"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineShowingAPageKeepsItAndUpdatesTheSource()
    {
        using var engine = new BrowserEngine();
        await engine.NavigateAsync(BluePage);
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var created = view.Engine!;

        view.Engine = engine;

        Assert.That(view.Source, Is.EqualTo(BluePage));
        Assert.That(engine.Title, Is.EqualTo("Blue"));
        Assert.That(await IsDisposedAsync(created), Is.True);
        window.Close();
    }

    [AvaloniaTest]
    public async Task AFreshEngineOpensTheSource()
    {
        using var engine = new BrowserEngine();
        var view = new BrowserView { Engine = engine, Source = RedPage };
        var window = Show(view);

        await WaitUntil(() => engine.Title == "Red");

        window.Close();
    }

    [AvaloniaTest]
    public async Task TheOptionsAreUsedForTheCreatedEngine()
    {
        var view = new BrowserView
        {
            Source = RedPage,
            Options = new BrowserOptions { ColorScheme = "dark", IsScriptingEnabled = false },
        };
        var window = Show(view);
        window.RequestedThemeVariant = ThemeVariant.Light;
        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine!.ColorScheme, Is.EqualTo("dark"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task TheColorSchemeOfACreatedEngineFollowsTheTheme()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        window.RequestedThemeVariant = ThemeVariant.Light;
        await WaitUntil(() => view.Engine?.Title == "Red");
        Assert.That(view.Engine!.ColorScheme, Is.EqualTo("light"));

        window.RequestedThemeVariant = ThemeVariant.Dark;

        Assert.That(view.Engine.ColorScheme, Is.EqualTo("dark"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task ThePageIsDisplayed()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);

        await WaitUntil(() =>
        {
            using var frame = window.CaptureRenderedFrame();
            return frame is not null && IsRed(GetCenterPixel(frame));
        });

        window.Close();
    }

    private static string CreatePage(string title, string background) =>
        "data:text/html," + Uri.EscapeDataString($"<!doctype html><html style='background:{background}'><title>{title}</title></html>");

    private static Window Show(BrowserView view)
    {
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);

        while (!condition())
        {
            if (DateTime.UtcNow > timeout)
            {
                Assert.Fail("The condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    // an awaited try/catch, as the asserts for async code block the dispatcher running the test
    private static async Task<bool> IsDisposedAsync(BrowserEngine engine)
    {
        try
        {
            await engine.PrepareFrameAsync();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool IsRed(byte[] bgra) => bgra[2] > 200 && bgra[1] < 50 && bgra[0] < 50;

    private static unsafe byte[] GetCenterPixel(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var row = (byte*)buffer.Address + buffer.RowBytes * (buffer.Size.Height / 2);
        var pixel = row + 4 * (buffer.Size.Width / 2);
        // the channel order depends on the platform; red and blue are what the test tells apart
        var isBgra = buffer.Format == PixelFormat.Bgra8888;
        return isBgra ? [pixel[0], pixel[1], pixel[2], pixel[3]] : [pixel[2], pixel[1], pixel[0], pixel[3]];
    }
}
