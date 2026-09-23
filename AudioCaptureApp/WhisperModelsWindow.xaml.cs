using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// Whisper モデルの管理ダイアログ（REQ-MODELWIN-01〜06）。
/// </summary>
/// <remarks>
/// 自前の状態を持たず、<c>MainWindow</c> と同じ <see cref="MainViewModel"/> インスタンスを
/// <c>DataContext</c> として共有する（<c>docs/adr/0002-secondary-windows-share-mainviewmodel.md</c>、
/// <c>docs/adr/0006-mainviewmodel-split-reevaluation.md</c>）。生成は <c>SettingsWindow</c> が行う
/// （モーダルな設定ウィンドウの上に出すため。ADR-0006 規則 4）。
/// 変更はその場で保存されるため、取り消しの手段は設けない（REQ-MODELWIN-06）。
/// </remarks>
public partial class WhisperModelsWindow : Window
{
    public WhisperModelsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}