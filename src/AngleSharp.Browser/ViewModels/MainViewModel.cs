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
        if (Engine.Url is string url && url != "about:blank")
        {
            Address = url;
        }

        WindowTitle = string.IsNullOrWhiteSpace(Engine.Title) ? ApplicationName : $"{Engine.Title} - {ApplicationName}";
        IsLoading = Engine.IsLoading;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        Status = Engine.HoveredLink ?? (IsLoading ? $"Loading {Address}" : _lastError ?? "Done");
    }
}
