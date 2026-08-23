# T135 — 書き出し完了時の行バーストを間引く

> **状態:** 完了 — 2026-08-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [T115](./T115-speaker-diarization.md)（発生源）／[T130](./T130-live-transcript-window-tweaks.md)（表示上限 100 行）／
> [T128](./T128-live-transcript-scroll-crash.md)（スクロールの後回し）

## 1. 目的

話者ダイアライゼーションが有効なとき、`SegmentTranscribed` は書き出しの最後に全行をまとめて発火する
（話者の割り当てはタイムライン全体が揃うまで確定しないため）。1 時間の会議なら 1,500〜2,100 行が
連続で `Dispatcher.BeginInvoke` に積まれる一方、表示上限は 100 行なので**大半は積まれた直後に捨てられる**。
実機で測り、対処が要るかを判断する。

## 2. スコープ境界

**やること**

- 表示ウィンドウと同じ構成（`ListBox` ＋ `ObservableCollection` ＋ `CollectionChanged` →
  `BeginInvoke(Background)` で `ScrollIntoView`）を再現して、バーストの費用を測る
- 候補（間引き／まとめて 1 回の発火）を実測で比較する
- 効果が確認できた方式だけを実装する

**やらないこと（重要）**

- **`TranscriptionService` 側は変更しない。** `SegmentTranscribed` は 1 行ずつ発火したままにする。
  表示上限（`MaxLiveTranscriptLines`）は UI の都合であり、Service に知らせる筋合いのものではない。
- **表示上限そのものは変えない**（REQ-LIVEVIEW-04 の 100 行は T130 の決定）。
- **スクロールの仕組みには触れない**（REQ-LIVEVIEW-07 / T128）。
- **ライブ文字起こしの見え方を変えない。** 1 行ずつ届く経路では従来と同一の挙動にする。

## 3. 決定事項

| # | 選択が必要だった点 | 結論 |
|---|---|---|
| D1 | そもそも対処するか | **する。** 表示ウィンドウを開いた状態で 2,100 行を流すと、捌け切るまで **665〜735ms**、UI の詰まり最大 **177ms**（§8-1）。完了通知の直後に 0.7 秒もたつく |
| D2 | 「まとめて 1 回の `BeginInvoke`」にするか | **しない。効かない。** 実測 667ms（現行 665ms）とほぼ同じ。費用は Dispatcher のキューではなく、**1 行ごとの `CollectionChanged` → レイアウト → `ScrollIntoView`** だからである |
| D3 | 間引きをどこでやるか | **`MainViewModel`。** 溜めて 1 回で引き取り、**表示上限を超えるぶんは `ObservableCollection` へ追加せずに捨てる**。実測 143ms（4.6 倍速く、100ms を超える詰まりが 0 回になる） |
| D4 | 捨ててよいと言い切れる根拠 | **最終状態が 1 行ずつ追加した場合と一致するため。** 追加してから `AppendLiveTranscriptLine` に捨てさせても、先に捨てても、残る 100 行は同じである。テストで固定した（`AppendLiveTranscriptLines_SameResultAsOneByOne`） |
| D5 | 引き取りの予約フラグを下ろす順序 | **取り出しより先に下ろす。** 逆にすると、取り出しの最中に積まれた行が次の 1 行が届くまで画面に出ない。空振りの引き取りが 1 回増えるが無害である |
| D6 | 録音開始時のクリア | **キューも空にする**（REQ-LIVEVIEW-08）。消し忘れると前のセッションの行がクリア直後に現れる |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — `REQ-LIVEVIEW-09` を新設（バースト時の間引き）
- [x] `docs/spec/03_class_diagram.md` — `MainViewModel` に `AppendLiveTranscriptLines` を追加
      （テストから直接呼ぶ `internal static` ヘルパーは図に載せる規約である）
- 変更なし: `02_architecture.md`（層構成・スレッドモデルは不変。ワーカー → キュー → Dispatcher の
  向きは従来と同じで、間に溜める場所が増えただけである）

## 5. アーキテクチャへの影響

**ADR 不要。** 層構成・依存方向・スレッドモデルのいずれも変わらない。
NFR-01（UI スレッド以外からバインドプロパティを更新しない）は従来どおり守られている
— `QueueLiveTranscriptLine` はワーカースレッドで動くが、触るのは `ConcurrentQueue` だけで、
`LiveTranscriptLines` に触れるのは `Dispatcher` 上の `FlushLiveTranscriptLines` のみである。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `SegmentTranscribed` の受け口を `QueueLiveTranscriptLine` へ。`FlushLiveTranscriptLines` と `AppendLiveTranscriptLines` を追加。`StartRecording` でキューも空にする |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 間引きのテスト 5 件を追加 |
| `docs/spec/01_requirements.md` | `REQ-LIVEVIEW-09` |
| `docs/spec/03_class_diagram.md` | `AppendLiveTranscriptLines` を追加 |

## 7. 実装手順

- [x] **A1** 表示ウィンドウと同じ構成の計測ハーネスを scratchpad に作る
- [x] **A2** 現行方式・まとめて 1 回・間引き・ウィンドウ非表示の 4 通りを実測する
- [x] **B1** `AppendLiveTranscriptLines`（上限を超えるぶんを追加しない）を足す
- [x] **B2** `QueueLiveTranscriptLine` / `FlushLiveTranscriptLines` を足して結線する
- [x] **B3** `StartRecording` のクリアにキューを含める
- [x] **C1** テストを追加（間引き・1 行ずつとの一致・境界・空バッチ）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

`MainViewModelTests`

- **`AppendLiveTranscriptLines_OverLimit_AddsOnlyTheNewest`** — 上限を超えるぶんを
  **追加せずに**捨てること（本タスクの核心）
- **`AppendLiveTranscriptLines_SameResultAsOneByOne`** — 間引いても最終状態が
  1 行ずつ追加した場合と一致すること（D4 の根拠。ここが崩れると「見えるものが変わる」最適化になる）
- **`AppendLiveTranscriptLines_UnderLimit_KeepsExistingAndTrims`** — 上限より短いバッチでは
  1 行も捨てず、既存の行と合わせて上限で切ること
- **`AppendLiveTranscriptLines_SingleLine_BehavesLikeAppendLiveTranscriptLine`** —
  ライブ経路（1 行ずつ）の挙動が変わらないこと
- **`AppendLiveTranscriptLines_EmptyBatch_ChangesNothing`** — 空振りの引き取りで表示を壊さないこと

> **テストで守れない範囲:** UI の詰まり時間そのもの。WPF の実レイアウトが要るためユニットテストにできない。
> §8-1 の計測ハーネス（scratchpad）で測った。

### 8-1. 実測（WPF・表示ウィンドウを再現したハーネス）

`ListBox` ＋ `ObservableCollection` ＋ `CollectionChanged` → `BeginInvoke(Background)` で
`ScrollIntoView`、`ItemTemplate` は折り返す `TextBlock`（`LiveTranscriptWindow.xaml` と同じ）。
UI の詰まりは 50ms 間隔の `DispatcherTimer`（本体のレベルメーターと同じ）の実間隔で測る。

| 方式 | 行数 | 捌け切るまで | 50ms タイマーの最大間隔 | 100ms 超 |
|---|---|---|---|---|
| **現行**（1 行ずつ `BeginInvoke`） | 2,100 | 665 / 735ms | 145 / 177ms | 1 回 |
| 現行 | 1,500 | 550ms | 132ms | 1 回 |
| まとめて 1 回の `BeginInvoke`（D2） | 2,100 | **667ms** | 159ms | 1 回 |
| **本タスク**（間引き。上限ぶんだけ追加） | 2,100 | **143ms** | **99ms** | **0 回** |
| 参考: 表示ウィンドウを開いていない場合 | 2,100 | 58ms | 42ms | 0 回 |

読み取れること：

1. **表示ウィンドウを開いていなければ問題は起きない**（58ms）。バーストが効くのは
   ウィンドウを開いているときだけである。
2. **「まとめて 1 回の発火」は効かない**（667ms）。ticket に候補として挙がっていたが、
   費用の出どころが違う。1 行ごとのレイアウトが本体である。
3. **間引きは効く**（143ms、4.6 倍）。100ms を超える詰まりが消える。

## 9. 未解決の質問

なし。

## 10. 前提

- 1 時間の会議の行数は、実機の `.transcript.txt` の実績（1,429〜2,088 行）から 2,100 行を上限として使った。
- 計測は開発機（このリポジトリのビルド環境）で行った。低速機ではいずれの数字も伸びるが、
  方式間の比（現行 : 間引き ≒ 5 : 1）は変わらない。

## 実行結果 (2026-08-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 195 件成功 / 0 件失敗 / 0 件スキップ（本タスクで追加 5 件。既存 190 件は無改修で通過）

### 計画からの逸脱

1. **起票時に候補として挙がっていた「まとめて 1 回の発火」を採らなかった。**
   実測で効果が無い（667ms / 665ms）と分かったため。§8-1 に数字を残した。
