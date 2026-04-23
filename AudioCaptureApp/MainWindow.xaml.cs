using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private bool TryGetSingleDroppedFile(DragEventArgs e, out string filePath)
    {
        filePath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            return false;
        }
        filePath = files[0];
        return true;
    }

    private void TranscriptionGroup_DragOver(object sender, DragEventArgs e)
    {
        bool accept = TryGetSingleDroppedFile(e, out _) && _viewModel.CanAcceptFileDrop;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void TranscriptionGroup_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void TranscriptionGroup_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetSingleDroppedFile(e, out var filePath))
        {
            return;
        }
        e.Handled = true;
        await _viewModel.TranscribeDroppedFileAsync(filePath);
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}