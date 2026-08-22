using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel = new();
    private LiveTranscriptWindow? _liveTranscriptWindow;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 補助ウィンドウの生成は View 層の責務（ADR-0002）。ViewModel はイベントで要求だけを上げる。
        _viewModel.FileTranscriptionRequested += ShowFileTranscriptionOptions;
        _viewModel.LiveTranscriptRequested += ShowLiveTranscript;
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

    /// <summary>
    /// 文字起こし表示ウィンドウを開く。同時に 1 つだけ持ち、既に開いていれば手前に出す
    /// （REQ-LIVEVIEW-06）。<c>Owner</c> の設定により、メインウィンドウを閉じると
    /// 一緒に閉じる（REQ-LIVEVIEW-05）。
    /// </summary>
    private void ShowLiveTranscript()
    {
        if (_liveTranscriptWindow == null)
        {
            _liveTranscriptWindow = new LiveTranscriptWindow(_viewModel) { Owner = this };
            _liveTranscriptWindow.Closed += (_, _) => _liveTranscriptWindow = null;
            _liveTranscriptWindow.Show();
        }

        _liveTranscriptWindow.Activate();
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