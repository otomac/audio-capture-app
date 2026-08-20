# T128 — 文字起こし表示ウィンドウの自動スクロールでプロセスが落ちる

> **状態:** 完了 — 2026-08-21
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

T114 で入れた文字起こし表示ウィンドウが、行が連続で届くと
`InvalidOperationException`（「ItemsControl が項目のソースと一致していません」）を
UI スレッドの未処理例外として投げ、**プロセスごと落ちる**。録音中なら録音セッションを失う。
根本原因を直す。

## 2. 現象と証拠

利用者の実機で発生。Windows イベントログ（`.NET Runtime` ID 1026、2026/08/21 1:12:49）:

```
Application: AudioCaptureApp.exe
Description: The process was terminated due to an unhandled exception.
System.InvalidOperationException: ItemsControl が項目のソースと一致していません。
 ---> 'LinesList' のジェネレーターで、項目コレクションの現在の状態に同意しない
      CollectionChanged イベントのシーケンスを受け取った
      累計カウント 8 が実際のカウント 9 と異なります
   at System.Windows.Controls.ItemContainerGenerator.Verify()
   at System.Windows.Controls.VirtualizingStackPanel.MeasureChild(...)
   at System.Windows.ContextLayoutManager.UpdateLayout()
   at System.Windows.Controls.ItemsControl.OnBringItemIntoView(ItemInfo info)
   at System.Windows.Controls.ListBox.ScrollIntoView(Object item)
   at AudioCaptureApp.LiveTranscriptWindow.OnLinesChanged(...) LiveTranscriptWindow.xaml.cs:line 36
   at System.Collections.ObjectModel.ObservableCollection`1.OnCollectionChanged(...)
   at AudioCaptureApp.ViewModels.MainViewModel.AppendLiveTranscriptLine(...) MainViewModel.cs:line 282
```

対応する録音の `.txt`（`20260821_010918.txt`）では、スピーカーの 3 行が
01:12:23 / 01:12:34 / 01:12:41 と **1 チャンクから立て続けに**出ており、直後に落ちている。
マイクの行は 1 行ずつ間隔が空いていたため、それまでは落ちなかった。

## 3. 根本原因

`OnLinesChanged` が `CollectionChanged` ハンドラーの**中で同期的に** `ScrollIntoView` を呼んでいた。

1. `ScrollIntoView` は `ItemsControl.OnBringItemIntoView` → `UpdateLayout()` を走らせる。
2. `VirtualizingStackPanel.MeasureChild` が `ItemContainerGenerator.Verify()` を通る。
3. その時点でジェネレーターはまだ今回の Add を処理し終えていないため、
   累計カウント（8）と `ItemCollection.Count`（9）が食い違い、例外になる。

**なぜジェネレーターより先に自分のハンドラーが呼ばれるのか。**
ウィンドウのコンストラクターは

```csharp
InitializeComponent();      // ItemsSource="{Binding LiveTranscriptLines}" は未解決
DataContext = viewModel;    // バインディングの実接続は Dispatcher 経由で後回しになる
_viewModel.LiveTranscriptLines.CollectionChanged += OnLinesChanged;   // ← こちらが先に載る
```

の順で動くため、`CollectionChanged` の購読順が「自分 → WPF」になる。
これは WPF の内部都合であり、こちらから制御できない。

## 4. 再現と検証（実測）

音声も Whisper も使わず、WPF の再入だけを切り出した再現ハーネスを作って確認した
（`scrollrepro`。リポジトリには含めない一時コード）。`LiveTranscriptWindow.xaml` の
`ListBox` をそのまま `XamlReader` で読み、**コンストラクターと同じ順序**で組み立て、
実際に落ちたセッションの `.txt` から採った行をワーカースレッドから
`Dispatcher.BeginInvoke` で 3 行ずつ流し込む。

| モード | 内容 | 結果 |
|---|---|---|
| `sync` | 現行（ハンドラー内で同期 `ScrollIntoView`） | **5 回中 5 回クラッシュ**（実機と同一の例外） |
| `deferred` | 修正案（`Background` 優先度へ後回し） | 5 回中 0 回クラッシュ（24 行追加） |
| `deferred-close` | 修正案＋行の流入中にウィンドウを閉じる | 5 回中 0 回クラッシュ |

`ItemsSource` を直接代入する（＝購読順が「WPF → 自分」になる）組み方では**再現しない**ことも
確認した。購読順が原因であることの裏付けになっている。

さらに、コピーではなく **実物の `LiveTranscriptWindow` と `MainViewModel`** を参照して
同じ流し方をする確認も行った（`realwindow`。これも一時コード）。

| 対象 | 結果 |
|---|---|
| 修正前の `LiveTranscriptWindow`（`git show HEAD:` で戻したもの） | **3 回中 3 回クラッシュ** |
| 修正後の `LiveTranscriptWindow` | 3 回中 0 回クラッシュ（30 行追加） |

対照実験になっているので、「0 回だった」がハーネスの感度不足ではないことを示せている。

## 5. スコープ境界

**やること**
- `OnLinesChanged` のスクロールを `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` へ回す。

**やらないこと（重要）**
- **`try`/`catch` で握り潰さない。** 症状ではなく原因を直す。
- **`VirtualizingStackPanel.IsVirtualizing="False"` にして回避しない。** 1,000 行（REQ-LIVEVIEW-04）を
  非仮想化で抱えるのは別の問題を作る。
- **UI スレッドの全体的な未処理例外ハンドラーは今回入れない。** §9 に積み残しとして記録する。
- **`MainViewModel` 側（`AppendLiveTranscriptLine`）は変えない。** あちらは正しい。

## 6. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 遅延に使う優先度 | **`DispatcherPriority.Background`。** レイアウト（`Render` / `Loaded`）より低いので、ジェネレーターの処理とレイアウトが終わってから走ることが保証できる。実測で 0/5 |
| D2 | 連続で行が来ると `BeginInvoke` が複数積まれる点 | **そのままにする。** 各コールバックは常に「最後の行」へスクロールするだけなので冪等。合体させる仕組みは今の行数では過剰 |
| D3 | 購読順そのものを変えて解決するか | **しない。** `ItemsSource` の接続タイミングは WPF の内部都合で、順序に依存した書き方は壊れやすい。順序に依存しない「後回し」で解く |

## 7. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-LIVEVIEW-06 に禁止事項を追記し、
      REQ-LIVEVIEW-07 を新設（後回し実行の理由まで残す。将来「簡潔に」戻されるのを防ぐため）

## 8. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/LiveTranscriptWindow.xaml.cs` | `OnLinesChanged` のスクロールを `Background` 優先度へ後回し。理由を doc コメントに残す |

> **テストで守れない範囲:** この不具合は WPF のウィンドウ・仮想化パネル・Dispatcher が
> 揃って初めて起きるため、`AudioCaptureApp.Tests`（純粋関数を対象とする方針、
> [20-architecture-standards.md §7](../harness/20-architecture-standards.md)）では固定できない。
> 代わりに §4 の再現ハーネスで 5 回×3 モードの実測を取った。
> 回帰を防ぐのは REQ-LIVEVIEW-07 とソース中の doc コメントである。

## 9. 積み残し・気づいたこと

- **表示専用のサブウィンドウのバグで、録音中のセッションごとプロセスが落ちた。**
  NFR-05（回復可能なエラーでアプリを落とさない）の趣旨からすると、UI スレッドの
  未処理例外を受け止めて録音を守る仕組み（`Application.DispatcherUnhandledException`）を
  検討する価値がある。ただし入れ方を誤ると今回のようなバグを隠すため、今回は**入れていない**。
  必要なら別タスクで起票する。
- **T114 の起動確認（exe を起動して終了）ではこの不具合を検出できなかった。**
  ウィンドウを開いて行を流し込むところまでやらないと出ない。今後 UI を足すときは、
  §4 のような小さな再現ハーネスを先に作るほうが早い。

---

## 実行結果 (2026-08-21)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 121 件成功 / 0 件失敗 / 0 件スキップ
- 再現ハーネス  : `sync` 5/5 クラッシュ → `deferred` 0/5 / `deferred-close` 0/5
- 実物での確認  : 修正前 3/3 クラッシュ → 修正後 0/3（30 行追加）
- 起動確認      : `AudioCaptureApp.exe` を起動 → 稼働継続 → 正常終了
- 計画からの逸脱: なし
