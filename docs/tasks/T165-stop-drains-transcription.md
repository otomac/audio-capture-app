# T165 — 文字起こしが遅延した状態で録音を停止すると、最後まで文字起こしされずに終わる

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

停止時に滞留していた文字起こしを捨てない。音声（`.mp3`）は保存済みで、文字起こしだけを取りこぼすのが実害。

## 2. スコープ境界

**やること**
- ①実機で再現し、停止時に残っていた秒数を測る（起票時の条件）
- ②「停止しなくても最後まで出ない」を切り分ける
- ③停止処理を 30 秒で打ち切らず、滞留分を吐き切るまで「停止処理中（残り N 秒分）」を出し続ける
- 待ちきれないときの明示的な「打ち切り」（従来のキャンセル経路）

**やらないこと（重要）**
- **チャンクの確定条件（REQ-TRX-LIVE-04 / 10 / 12 / 13）は変えない**
- **T117 の安全策（ワーカー未終了時は `WhisperProcessor` を破棄しない）は変えない**
- **停止処理中に別の操作を開放しない**（REQ-REC-09 のまま）

## 3. 再現（2026-09-22、scratchpad `xamlcheck t165`。本体の `MainViewModel` をそのまま使用）

方法: 既定の再生デバイスをループバックで取り込み、15 分の会議音声を `WaveOutEvent` で再生。ライブ文字起こしは
`ggml-large-v3-turbo` を **CPU** で実行し、20 コアのうち 18 コアを占有するスレッドを回して遅いマシンを模した。
150 秒録音して停止。`PendingSeconds`（未処理の音声の長さ）を 10 秒ごとに記録。

| 条件 | 録音中の滞留 | 停止にかかった時間 | 残った行 | 捨てられた音声 |
|---|---|---|---|---|
| `medium`・CPU・占有なし | 10〜20 秒で一定（追いついている） | 10.0 秒 | 42 | 0 |
| `large-v3-turbo`・CPU・占有なし | 10〜20 秒で一定 | 9.0 秒 | 48 | 0 |
| `large-v3-turbo`・CPU・18 コア占有 | **10 秒ごとに約 8 秒ずつ増え、停止時 113.8 秒** | 36.1 秒（30 秒待ち + キャンセル） | **2** | **113.8 秒分**（150 秒中） |

- 読みどおり `StopSession` の 30 秒（`StopGraceTimeout`）で打ち切られ、`_cts.Cancel()` で残りが捨てられた。
- ②「停止しなくても最後まで出ない」は**再現しなかった**。占有なしの 2 条件では録音中の滞留が 20 秒を超えず、
  行は出続けた。占有ありでも滞留は増え続けるだけで捨てられてはいない（`PendingSeconds` が単調に増える）。
  報告の現象は「停止で捨てられた」を「出なかった」と観察したものと考えられる（**推定**）。

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 停止の上限 | **無くす。** `StopSession` はワーカーが自然に終わるまで 250ms 間隔で待つ。打ち切り要求が来たときだけ従来の経路（キャンセル + 10 秒待ち + 未終了なら破棄見送り） |
| D2 | 残りの表示 | `TranscriptionService.PendingSeconds`（確定済みチャンク + 未確定バッファ + 処理中のチャンク）を、メーターの 50ms タイマーから 1 秒ごとに `StatusMessage` へ「停止処理中... 文字起こしの残り N 秒分」と出す。専用の欄は増やさない |
| D3 | 打ち切りの導線 | 「■ 停止」と同じ位置・寸法の「打ち切り」ボタン（停止処理中だけ表示。NFR-09）。押したら残り秒数を示して確認し、`RequestAbort()`。押した後は無効化 |
| D4 | プロセス終了 | `Dispose` は待たずに打ち切る（閉じる経路は `ShutdownAsync` が先に待つので、通常はここに滞留は無い） |
| D5 | 打ち切りの確認 | View（`MainWindow`）が `MessageBox` で行う（`CloseConfirmationMessage` と同じ形。ViewModel は文言を返すだけ） |
| D6 | `_sources` の錠 | `PendingSeconds` を UI スレッドから読むため、要素の増減（登録・停止・破棄）だけを `_sourcesLock` で守る。ワーカーの列挙は要素を増減しないので錠を取らない |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-11（上限なし・打ち切り・実測）、REQ-REC-07（残り表示・打ち切りボタン）
- [x] `docs/spec/03_class_diagram.md` — `RequestAbort` / `PendingSeconds`、`MainViewModel` の追加メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §4 録音停止の待ち方
- 変更なし: `02_architecture.md`（スレッドモデルは同じ。UI スレッドが読むのはカウンターだけ）

## 6. アーキテクチャへの影響

- ADR: **不要**。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `StopSession` の待ち方、`RequestAbort`、`PendingSeconds`、`_inFlightSamples`、`_sourcesLock`、`Dispose` |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs` | `TranscriptionPendingSeconds` / `IsStopAbortRequested` / `AbortStop` / `StoppingStatusFor` / `UpdateStoppingStatus` |
| `AudioCaptureApp/ViewModels/MainViewModel.Devices.cs` | タイマーからの呼び出し |
| `AudioCaptureApp/MainWindow.xaml(.cs)` | 「打ち切り」ボタンと確認 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 文言のテスト |

## 8. テスト一覧

- **`StoppingStatusFor_WithPending_ShowsCeiledSeconds`** / **`StoppingStatusFor_NothingPending_IsPlain`**
- **`AbortStopConfirmationMessage_MentionsPendingSeconds`**

> **テストで守れない範囲:** 待ち方そのもの（Whisper 実体が要る）。§9 の実測で確認する。

## 9. 修正後の実測（2026-09-22、§3 と同じ手順・同じ 18 コア占有）

| 条件 | 停止にかかった時間 | 残った行 | 備考 |
|---|---|---|---|
| 150 秒録音、停止時の滞留 113.8 秒、打ち切りなし | **1370.5 秒**（占有スレッドを回したまま） | **43**（修正前 2） | ステータスは「停止処理中... 文字起こしの残り 114 秒分」から 1 秒ごとに減り、最後の行は録音末尾（`[09:50:59 - 09:51:01]`）まで届いた |
| 90 秒録音、停止時の滞留 91.5 秒、15 秒後に「打ち切り」 | **27.4 秒**（打ち切り要求から 11 秒） | 0 | 「文字起こしを打ち切って停止しています...」→「保存完了」。占有下では 10 秒でワーカーが抜けず、T117 の安全策（`WhisperProcessor` の破棄見送り）が働いた。その通知は完了の 1 行に併記される（D7） |

- 1370 秒は占有スレッド（20 コア中 18 を `AboveNormal` で回し続ける）の下での値で、実運用の目安ではない。
  同じ条件で修正前は 36 秒で 2 行しか残らなかった。**「待つか、捨てるか」を利用者が選べる**ようになったことが本質。
- 起票時の懸念「無条件に伸ばすと『止まらない』に戻る」には、打ち切りの導線と `Dispose` での打ち切り（プロセス終了）で答えた。

| # | 決定（追加） | 結論 |
|---|---|---|
| D7 | 停止中の `Error` 通知 | 打ち切りのタイムアウト等は `BeginInvoke` で書かれた直後に「保存完了」が上書きする（T134 と同じ順序）。`_lastTranscriptionError` に控え、「保存完了: … — <通知>」と併記する |

## 10. 未解決の質問

なし。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 320 件成功 / 0 件失敗 / 0 件スキップ（317 → 320、+3）
- 計画からの逸脱: D7（停止中の `Error` 通知の併記）を実測で見つけて足した。README に「打ち切り」の説明を 1 行足した
