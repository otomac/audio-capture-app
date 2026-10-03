using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// Whisper モデルの管理ダイアログ（REQ-MODELWIN-01〜06）。
/// </summary>
/// <remarks>
/// <c>DataContext</c> は <see cref="WhisperModelsViewModel"/>（<see cref="SettingsViewModel.WhisperModelsManager"/>。
/// <c>docs/adr/0008-per-window-viewmodels.md</c>）。生成は <c>SettingsWindow</c> が行う
/// （モーダルな設定ウィンドウの上に出すため。ADR-0006 規則 4 を ADR-0008 が引き継ぐ）。
/// 変更はその場で保存されるため、取り消しの手段は設けない（REQ-MODELWIN-06）。
/// </remarks>
public partial class WhisperModelsWindow : Window
{
    public WhisperModelsWindow(WhisperModelsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}