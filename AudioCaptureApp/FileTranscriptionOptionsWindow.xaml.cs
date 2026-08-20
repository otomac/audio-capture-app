using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// ファイル文字起こしのオプション指定ダイアログ（REQ-TRX-FILE-09）。
/// </summary>
/// <remarks>
/// 自前の状態を持たず、<c>MainWindow</c> と同じ <see cref="MainViewModel"/> インスタンスを
/// <c>DataContext</c> として共有する（<c>docs/adr/0002-secondary-windows-share-mainviewmodel.md</c>）。
/// 「開始」を押すと同じウィンドウが進捗表示へ切り替わり（REQ-TRX-FILE-11）、
/// 処理が終わったら完了・失敗・中止のいずれでも自動的に閉じる（REQ-TRX-FILE-12）。
/// </remarks>
public partial class FileTranscriptionOptionsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public FileTranscriptionOptionsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // async void はイベントハンドラーのみ許可（20-architecture-standards.md §3-4）。
    // StartFileTranscriptionAsync は内部で全例外を StatusMessage へ変換するため、
    // ここまで例外は伝播しない。
    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartFileTranscriptionAsync();

        // 処理中に利用者がこのウィンドウを閉じていた場合、Close() は何もしない
        // （そのときはメインウィンドウ側の進捗表示が引き継いでいる。REQ-TRX-FILE-13）。
        Close();
    }
}