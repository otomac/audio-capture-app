using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// 設定ウィンドウ（REQ-SETWIN-01〜06）。
/// </summary>
/// <remarks>
/// 自前の状態を持たず、<c>MainWindow</c> と同じ <see cref="MainViewModel"/> インスタンスを
/// <c>DataContext</c> として共有する（<c>docs/adr/0002-secondary-windows-share-mainviewmodel.md</c>、
/// および <c>docs/adr/0005-mainviewmodel-split.md</c>）。
/// コードビハインドに持ってよいのはウィンドウ自身の生存管理だけで、業務判断は置かない。
/// 変更は変更した時点で保存済み（REQ-CFG-05）なので、取り消しの手段は設けない（REQ-SETWIN-06）。
/// <para>
/// モデル管理ダイアログ（REQ-MODELWIN-01）だけはこのウィンドウが生成する — モーダルな設定ウィンドウの
/// 上に出すため、<c>Owner</c> を設定ウィンドウにする必要がある（ADR-0006 規則 4）。
/// 購読は開いている間だけにし、閉じるときに外す。
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.WhisperModelsRequested += ShowWhisperModels;
        Closed += (_, _) => _viewModel.WhisperModelsRequested -= ShowWhisperModels;
    }

    /// <summary>モデル管理ダイアログをモーダルで開く（REQ-MODELWIN-01）。</summary>
    private void ShowWhisperModels()
    {
        var dialog = new WhisperModelsWindow(_viewModel) { Owner = this };
        dialog.ShowDialog();
    }
}