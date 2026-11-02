using System;
using System.ComponentModel;
using Vision.ViewModels;
using Avalonia.Controls;

namespace Vision.Views;

public partial class MainWindow : Window
{
    private const double DefaultDiagnosticsWidth = 200;
    private const double MinDiagnosticsWidth = 160;

    private MainViewModel? _viewModel;
    private double _diagnosticsWidth = DefaultDiagnosticsWidth;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDiagnosticsOpen))
        {
            var column = ContentGrid.ColumnDefinitions[2];

            if (_viewModel!.IsDiagnosticsOpen)
            {
                // the splitter resizes the column; the width is remembered for the next time
                column.MinWidth = MinDiagnosticsWidth;
                column.Width = new GridLength(_diagnosticsWidth);
            }
            else
            {
                _diagnosticsWidth = column.ActualWidth > 0 ? column.ActualWidth : _diagnosticsWidth;
                column.MinWidth = 0;
                column.Width = new GridLength(0);
            }
        }
    }
}
