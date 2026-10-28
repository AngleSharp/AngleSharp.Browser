namespace AngleSharp.Browser.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using Avalonia.Threading;

public partial class MainViewModel : ViewModelBase
{
    private const string ApplicationName = "AngleSharp.Browser";

    private string? _lastError;
    private string? _lastUrl;

    public MainViewModel(BrowserEngine engine, string startAddress)
    {
        Engine = engine;
        Address = startAddress;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateState);
        engine.HoverChanged += (_, _) => Dispatcher.UIThread.Post(UpdateStatus);
        engine.Error += (_, ex) => Dispatcher.UIThread.Post(() =>
        {
            _lastError = ex.Message;
            UpdateStatus();
        });
    }

    public BrowserEngine Engine { get; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = ApplicationName;

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    private Task NavigateAsync()
    {
        _lastError = null;
        Address = BrowserEngine.NormalizeAddress(Address);
        return Engine.NavigateAsync(Address);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => Engine.GoBackAsync();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => Engine.GoForwardAsync();

    [RelayCommand]
    private Task ReloadAsync() => Engine.ReloadAsync();

    private bool CanGoBack() => Engine.CanGoBack;

    private bool CanGoForward() => Engine.CanGoForward;

    private void UpdateState()
    {
        // only a new address replaces what may be typed into the box
        if (Engine.Url is string url && url != "about:blank" && url != _lastUrl)
        {
            _lastUrl = url;
            Address = url;
        }

        WindowTitle = string.IsNullOrWhiteSpace(Engine.Title) ? ApplicationName : $"{Engine.Title} - {ApplicationName}";
        // a page has loaded once it is visible, which needs its first frame
        IsLoading = Engine.IsLoading || !Engine.HasPaintedDocument;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        Status = Engine.HoveredLink ?? (IsLoading ? $"Loading {Address}" : _lastError ?? "Done");
    }
}
