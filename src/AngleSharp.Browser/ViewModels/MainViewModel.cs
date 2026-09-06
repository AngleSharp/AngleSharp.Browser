namespace AngleSharp.Browser.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

public partial class MainViewModel : ViewModelBase
{
    private readonly Engine _engine;
    private CancellationTokenSource? _renderLoopCts;
    private Task? _renderLoopTask;

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(400);
    private const int RenderWidth = 1280;
    private const int RenderHeight = 2200;

    internal MainViewModel(Engine engine)
    {
        _engine = engine;
    }

    [ObservableProperty]
    public partial string Address { get; set; } = "https://example.org";

    [ObservableProperty]
    public partial Bitmap? PageImage { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NavigateAsync()
    {
        var urlText = NormalizeUrl(Address);

        await StopRenderLoopAsync();

        IsBusy = true;
        Status = $"Loading {urlText}";
        Address = urlText;
        NavigateCommand.NotifyCanExecuteChanged();

        _renderLoopCts = new CancellationTokenSource();
        _renderLoopTask = RunRenderLoopAsync(urlText, _renderLoopCts.Token);
    }

    private bool CanNavigate() => !string.IsNullOrWhiteSpace(Address);

    private async Task RunRenderLoopAsync(string url, CancellationToken cancellationToken)
    {
        var frames = 0;

        try
        {
            await _engine.StreamRendersAsync(
                url,
                RenderWidth,
                RenderHeight,
                RenderInterval,
                async pngBytes =>
                {
                    frames++;
                    await UpdateImageAsync(pngBytes).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Status = $"Rendered {url} (frame {frames})";
                    });
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Address == url)
                {
                    Status = $"Stopped rendering {url}";
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = $"Failed: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_renderLoopCts?.Token == cancellationToken)
                {
                    IsBusy = false;
                    NavigateCommand.NotifyCanExecuteChanged();
                }
            });
        }
    }

    private async Task UpdateImageAsync(byte[] pngBytes)
    {
        await using var memory = new MemoryStream(pngBytes);
        var bitmap = new Bitmap(memory);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var previous = PageImage;
            PageImage = bitmap;
            previous?.Dispose();
        });
    }

    private async Task StopRenderLoopAsync()
    {
        var cts = _renderLoopCts;
        var task = _renderLoopTask;

        if (cts is null || task is null)
        {
            return;
        }

        cts.Cancel();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when navigating to a new URL.
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_renderLoopCts, cts))
            {
                _renderLoopCts = null;
                _renderLoopTask = null;
            }
        }
    }

    private static string NormalizeUrl(string? input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "https://example.org";
        }

        return trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"https://{trimmed}";
    }
}
