using System.Collections.Specialized;
using System.Windows;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp;

/// <summary>
/// 文字起こし行を表示するサブウィンドウ（REQ-LIVEVIEW-01）。
/// </summary>
/// <remarks>
/// 自前の状態を持たず、<c>MainWindow</c> と同じ <see cref="MainViewModel"/> インスタンスを
/// <c>DataContext</c> として共有する（<c>docs/adr/0002-secondary-windows-share-mainviewmodel.md</c>）。
/// 録音停止で閉じる処理は<b>意図的に書いていない</b>（REQ-LIVEVIEW-05）。
/// プロセス終了時に閉じるのは、生成側が <c>Owner</c> を設定していることによる WPF の既定動作。
/// </remarks>
public partial class LiveTranscriptWindow : Window
{
    private readonly MainViewModel _viewModel;

    public LiveTranscriptWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // ウィンドウを閉じても ViewModel は生き続けるため、購読を残すと参照が漏れる
        _viewModel.LiveTranscriptLines.CollectionChanged += OnLinesChanged;
        Closed += (_, _) => _viewModel.LiveTranscriptLines.CollectionChanged -= OnLinesChanged;
    }

    /// <summary>新しい行が届いたら最新行まで送る（REQ-LIVEVIEW-06）。</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LinesList.Items.Count > 0)
        {
            LinesList.ScrollIntoView(LinesList.Items[^1]);
        }
    }
}