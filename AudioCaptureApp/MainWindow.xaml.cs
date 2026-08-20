using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel = new();
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 補助ウィンドウの生成は View 層の責務（ADR-0002）。ViewModel はイベントで要求だけを上げる。
        _viewModel.FileTranscriptionRequested += ShowFileTranscriptionOptions;
        Closed += (_, _) => Dispose();
    }

    /// <summary>
    /// ファイル文字起こしのオプション指定ダイアログをモーダルで開く（REQ-TRX-FILE-09）。
    /// </summary>
    private void ShowFileTranscriptionOptions()
    {
        var dialog = new FileTranscriptionOptionsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    private static bool TryGetSingleDroppedFile(DragEventArgs e, out string filePath)
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

    private void TranscriptionGroup_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetSingleDroppedFile(e, out var filePath))
        {
            return;
        }
        e.Handled = true;
        // 処理はすぐには始まらない。オプション指定ダイアログの表示要求が上がるだけ（REQ-TRX-FILE-02）。
        _viewModel.TranscribeDroppedFile(filePath);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // ウィンドウは Closed イベントから 1 度だけ破棄される。
    // アンマネージドリソースは持たないため disposing == false のときは何もしない。
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }
        _disposed = true;
        _viewModel.Dispose();
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}