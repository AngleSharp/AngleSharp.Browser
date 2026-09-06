namespace AngleSharp.Browser;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AngleSharp.Browser.ViewModels;
using AngleSharp.Browser.Views;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            var screen = desktop.MainWindow?.Screens?.Primary;
            var engine = new Engine(clipboard!, screen!);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(engine),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}