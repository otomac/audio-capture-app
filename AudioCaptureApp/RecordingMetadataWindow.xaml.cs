using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// 録音停止後のメタデータ入力ダイアログ（REQ-REC-13 / §16）。
/// </summary>
/// <remarks>
/// <c>DataContext</c> は <see cref="RecordingMetadataViewModel"/>（<see cref="MainViewModel.RecordingMetadata"/>。
/// <c>docs/adr/0008-per-window-viewmodels.md</c>）。
/// 「OK」は <see cref="Window.DialogResult"/> を <c>true</c> にして閉じるだけで、JSON の書き出しと改名は
/// <c>MainWindow</c> が <see cref="RecordingMetadataViewModel.Complete"/> を呼んで行う。
/// 「キャンセル」と × はどちらも何も残さない（確認は挟まない — 残すものが無いため）。
/// </remarks>
public partial class RecordingMetadataWindow : Window
{
    public RecordingMetadataWindow(RecordingMetadataViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}