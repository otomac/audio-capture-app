using System.ComponentModel;
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

    /// <summary>終了確認（REQ-REC-11 / REQ-TRX-FILE-14）の進み具合。</summary>
    private enum CloseStage
    {
        /// <summary>まだ確認していない。</summary>
        NotAsked,

        /// <summary>「はい」の後、停止・中止の完了を待っている。</summary>
        ShuttingDown,

        /// <summary>後始末が済み、自分で <c>Close()</c> を呼び直した。</summary>
        Confirmed
    }

    private CloseStage _closeStage;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 補助ウィンドウの生成は View 層の責務（ADR-0002）。ViewModel はイベントで要求だけを上げる。
        _viewModel.FileTranscriptionRequested += ShowFileTranscriptionOptions;
        _viewModel.LiveTranscriptRequested += ShowLiveTranscript;
        Closing += MainWindow_Closing;
        Closed += (_, _) => Dispose();
    }

    /// <summary>
    /// 進行中の作業があれば確認してから閉じる（REQ-REC-11 / REQ-TRX-FILE-14）。
    /// </summary>
    /// <remarks>
    /// <c>Closing</c> は <c>await</c> できないため、いったん <see cref="CancelEventArgs.Cancel"/> で
    /// 閉じるのを取り消し、停止・中止が終わってから自分で <see cref="Window.Close"/> を呼び直す。
    /// 呼び直した <c>Close()</c> はこのハンドラーをもう一度通るので、
    /// <see cref="_closeStage"/> で素通しさせる。
    /// <para>
    /// async void はイベントハンドラーのみ許可（20-architecture-standards.md §3-4）。
    /// <see cref="MainViewModel.ShutdownAsync"/> は内部で全例外を握るため、ここまで例外は伝播しない。
    /// </para>
    /// </remarks>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeStage == CloseStage.Confirmed)
        {
            return;
        }

        if (_closeStage == CloseStage.ShuttingDown)
        {
            // 後始末の最中に × を押し直された。二重に走らせず、閉じるのだけを止める。
            e.Cancel = true;
            return;
        }

        var message = MainViewModel.CloseConfirmationMessage(
            _viewModel.IsRecording, _viewModel.IsStopping, _viewModel.IsTranscribingFile);
        if (message == null)
        {
            return;
        }

        e.Cancel = true;
        var answer = MessageBox.Show(
            this, message, "音声キャプチャ", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _closeStage = CloseStage.ShuttingDown;
        await _viewModel.ShutdownAsync();
        _closeStage = CloseStage.Confirmed;
        Close();
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