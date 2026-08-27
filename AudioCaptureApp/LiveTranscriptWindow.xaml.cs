using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    /// <summary>
    /// 新しい行が届いたら最新行まで送る（REQ-LIVEVIEW-06 / REQ-LIVEVIEW-07）。
    /// </summary>
    /// <remarks>
    /// <b>ここで <see cref="ListBox.ScrollIntoView"/> を直接呼んではならない（T128）。</b>
    /// <para>
    /// このハンドラーは WPF 側のジェネレーターより<b>先に</b>呼ばれる。ウィンドウの
    /// コンストラクターが <c>CollectionChanged</c> を購読するのは、XAML の
    /// <c>ItemsSource</c> バインディングが実際に接続されるより早いためである
    /// （バインディングの接続は <c>DataContext</c> 設定後に Dispatcher 経由で行われる）。
    /// この順序は WPF の内部都合であり、こちらから制御できない。
    /// </para>
    /// <para>
    /// その状態で <c>ScrollIntoView</c> を同期的に呼ぶと
    /// <c>ItemsControl.OnBringItemIntoView</c> → <c>UpdateLayout()</c> が走り、
    /// まだ変更を処理し終えていない <c>ItemContainerGenerator</c> の累計カウントと
    /// <c>ItemCollection.Count</c> が食い違って <c>Verify()</c> が
    /// <see cref="System.InvalidOperationException"/> を投げる。UI スレッドの未処理例外なので
    /// <b>プロセスごと落ちる</b>（録音中なら録音セッションを失う）。
    /// </para>
    /// <para>
    /// そこで <see cref="DispatcherPriority.Background"/> へ回し、ジェネレーターが変更を
    /// 処理し終えてレイアウトが落ち着いてからスクロールする。
    /// 再現ハーネスでの実測は 同期呼び出し 5/5 クラッシュ → 後回し 0/5 クラッシュ。
    /// </para>
    /// </remarks>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (LinesList.Items.Count > 0)
            {
                LinesList.ScrollIntoView(LinesList.Items[^1]);
            }
        });
    }
}