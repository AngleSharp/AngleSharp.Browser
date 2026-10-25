namespace AngleSharp.Browser;

using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AngleSharp.Browser.Integration;
using AngleSharp.Browser.ViewModels;
using AngleSharp.Browser.Views;

public partial class App : Application
{
    private const string DefaultStartAddress = "https://example.org";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var arguments = CommandLine.Parse(desktop.Args ?? []);
            var window = new MainWindow();
            var screen = window.Screens.Primary?.Bounds;
            var options = new BrowserOptions
            {
                ProfileDirectory = arguments.IsInMemory ? null : arguments.ProfileDirectory ?? GetDefaultProfileDirectory(),
                ClipboardPlatform = window.Clipboard is { } clipboard ? new ClipboardPlatform(clipboard) : null,
                ScreenWidth = screen?.Width ?? 1920,
                ScreenHeight = screen?.Height ?? 1080,
            };
            var engine = new BrowserEngine(options);
            var viewModel = new MainViewModel(engine, arguments.Address ?? DefaultStartAddress);

            window.DataContext = viewModel;
            window.Opened += (_, _) => viewModel.NavigateCommand.Execute(null);
            desktop.Exit += (_, _) => engine.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string GetDefaultProfileDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AngleSharp.Browser",
        "Default");

    /// <summary>
    /// Usage: AngleSharp.Browser [address] [--profile &lt;directory&gt;] [--in-memory]
    /// </summary>
    private sealed record CommandLine(string? Address, string? ProfileDirectory, bool IsInMemory)
    {
        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine(null, null, false);

            for (var i = 0; i < args.Length; i++)
            {
                result = args[i] switch
                {
                    "--in-memory" => result with { IsInMemory = true },
                    "--profile" when i + 1 < args.Length => result with { ProfileDirectory = args[++i] },
                    var address => result with { Address = address },
                };
            }

            return result;
        }
    }
}
