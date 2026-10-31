namespace AngleSharp.Browser.Views;

using System;
using System.Diagnostics;
using AngleSharp.Browser.Controls;
using AngleSharp.Browser.Pages;
using AngleSharp.Browser.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform;

/// <summary>
/// A window showing one of the browser's own HTML pages (settings, about), which it owns.
/// </summary>
public sealed class PageWindow : Window
{
    private readonly HtmlPage _page;
    private readonly IBrowserHost _host;

    public PageWindow(HtmlPage page, IBrowserHost host)
    {
        _page = page;
        _host = host;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AngleSharp.Browser/Assets/avalonia-logo.ico")));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new BrowserView { Engine = page.Engine };
        host.ColorSchemeChanged += OnColorSchemeChanged;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        try
        {
            await _page.LoadAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Loading the page failed: {ex}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _host.ColorSchemeChanged -= OnColorSchemeChanged;
        _page.Dispose();
    }

    private void OnColorSchemeChanged(object? sender, EventArgs e) => _page.Engine.ColorScheme = _host.ColorScheme;
}
