using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// 録音停止後のメタデータ入力ダイアログ（REQ-REC-13 / §16）。
/// </summary>
/// <remarks>
/// 自前の状態を持たず、<c>MainWindow</c> と同じ <see cref="MainViewModel"/> インスタンスを
/// <c>DataContext</c> として共有する（<c>docs/adr/0002-secondary-windows-share-mainviewmodel.md</c>、
/// <c>docs/adr/0006-mainviewmodel-split-reevaluation.md</c>）。
/// 「OK」は <see cref="Window.DialogResult"/> を <c>true</c> にして閉じるだけで、JSON の書き出しと改名は
/// <c>MainWindow</c> が <c>MainViewModel.CompleteRecordingMetadata</c> を呼んで行う。
/// 「キャンセル」と × はどちらも何も残さない（確認は挟まない — 残すものが無いため）。
/// </remarks>
public partial class RecordingMetadataWindow : Window
{
    public RecordingMetadataWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}