# T149 — 終了時の確認ダイアログ（録音中・文字起こし中）

> **状態:** 完了 — 2026-08-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

録音中・ファイル文字起こし中にウィンドウの × で閉じようとしたとき、確認を出して
「そのまま畳まれる」ことを防ぐ。YES なら通常の停止・中止処理を**最後まで通してから**終了し、
NO ならウィンドウを閉じない。

## 2. スコープ境界

**やること**
- `MainWindow` で `Closing` を捕まえ、進行中の作業があれば確認する。
- YES のとき、録音は通常の停止処理（`.mp3` の確定・`.txt` の追記）を通す。
  ファイル文字起こしは中止処理（REQ-TRX-FILE-07 = 生成中の出力ファイルの削除）を通す。
- 完了を待てるように、`MainViewModel` が実行中のタスクを保持する。
- **停止処理中（`IsStopping`）も対象に含める**（下の D3）。

**やらないこと（重要）**
- **`LiveTranscriptWindow` / `FileTranscriptionOptionsWindow` の × には手を入れない。**
  補助ウィンドウを閉じても本体の処理は続く（`FileTranscriptionOptionsWindow` の扱いは T151 の範囲）。
- **確認ダイアログを自前の `Window` にしない。** WPF 標準の `MessageBox` を使う（D2）。
- **`Dispose` の中身は変えない。** 終了直前に停止が終わっている状態を作るだけで、
  `Dispose` 側の安全網（`AudioCaptureService.Dispose` 等）はそのまま残す。
- **アプリのシャットダウン経路（タスクマネージャー・OS のログオフ）は対象外。**
  `Closing` が来ない終了は従来どおり `Dispose` 任せ。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | `Closing` は `await` できない。非同期の停止をどう待つか | **いったん `e.Cancel = true` で閉じるのを取り消し、停止・中止の完了後に自分で `Close()` を呼び直す。** 呼び直した `Close()` は状態フラグで素通しする |
| D2 | 確認 UI の作り | **`MessageBox.Show(Yes/No)`。** 自前の `Window` は作らない（ADR-0002 が対象とするのは状態を持つ補助ウィンドウで、モーダルな問い合わせは該当しない） |
| D3 | 「停止処理中（`IsStopping`）」を対象に含めるか | **含める。** 停止処理中もメインウィンドウは操作可能で × を押せる。ここで畳むと停止処理（MP3 の確定）と `Dispose` が競合する。文言は録音中とは別にし、YES なら**完了を待って**終了する |
| D4 | 判定ロジックの置き場 | **`MainViewModel.CloseConfirmationMessage(bool, bool, bool)` の `internal static` 純粋関数。** コードビハインドには「呼んで表示する」だけを残し、状態と文言の対応をテスト可能にする |
| D5 | 完了の待ち合わせ方法 | **`MainViewModel` が実行中の `Task` を保持する**（`_stopRecordingTask` / `_fileTranscriptionTask`）。`ShutdownAsync` はそれを `await` する。ポーリングやタイムアウト待ちにはしない |
| D6 | 録音と文字起こしが同時に動く場合 | **起こらない。** `CanStartRecording` は `!IsTranscribingFile`、`CanTranscribeFromFile` は `!IsRecording` を要求する。`ShutdownAsync` は念のため両方を順に処理するが、文言は 1 つだけ選ぶ |
| D7 | 停止・中止が失敗したとき終了するか | **する。** `StopRecordingAsync` / `RunFileTranscriptionAsync` は内部で全例外をステータス表示へ変換するため、`ShutdownAsync` は例外を投げない。失敗しても終了は続行する（閉じられないアプリにしない） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — §2 に **REQ-REC-11**（録音中・停止処理中の終了確認）、
      §9 に **REQ-TRX-FILE-14**（ファイル文字起こし中の終了確認）を新設
- [x] `docs/spec/03_class_diagram.md` — `MainViewModel` に `ShutdownAsync()` /
      `CloseConfirmationMessage(...)`、`MainWindow` に `MainWindow_Closing(...)` を追記
- [ ] `docs/spec/02_architecture.md` — 影響なし（層構成・スレッドモデルは変わらない）
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし（既存シーケンスの順序は変えない。
      終了確認は新しい入口であり、既存の録音停止シーケンスをそのまま呼ぶだけ）

## 5. アーキテクチャへの影響

- ADR: **不要。** 3 層構成・依存方向・スレッドモデルのいずれも変えない。
  View（`MainWindow`）が View の関心事（ウィンドウを閉じる）を扱い、
  ViewModel には状態と停止手順だけを持たせる既存の分担どおりである。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/MainWindow.xaml.cs` | `Closing` ハンドラーを追加。確認 → 停止待ち → `Close()` の呼び直し |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `CloseConfirmationMessage`（純粋関数）・`ShutdownAsync`・実行中タスクの保持を追加。`StopRecordingAsync` を薄いラッパーと本体に分ける |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `CloseConfirmationMessage` のテストを追加 |
| `docs/spec/01_requirements.md` | REQ-REC-11 / REQ-TRX-FILE-14 を追加 |
| `docs/spec/03_class_diagram.md` | 追加メソッドを反映 |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** §2 に REQ-REC-11 を追加 (`docs/spec/01_requirements.md`)
- [x] **A2** §9 に REQ-TRX-FILE-14 を追加 (`docs/spec/01_requirements.md`)
- [x] **A3** クラス図へ追加メソッドを反映 (`docs/spec/03_class_diagram.md`)

### グループ B — ViewModel
- [x] **B1** `CloseConfirmationMessage(bool isRecording, bool isStopping, bool isTranscribingFile)` を追加 (`MainViewModel.cs`)
- [x] **B2** `StopRecordingAsync` を「タスクを保持して本体を呼ぶラッパー」と `StopRecordingCoreAsync` に分ける
- [x] **B3** `StartFileTranscriptionAsync` で実行中タスクを保持する
- [x] **B4** `ShutdownAsync` を追加（中止 → 停止の順に待つ）

### グループ C — View
- [x] **C1** `Closing` ハンドラーを追加し、状態フラグで再入を防ぐ (`MainWindow.xaml.cs`)

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`CloseConfirmationMessage_Idle_ReturnsNull`** — 何も進行していなければ確認せずに閉じる
- **`CloseConfirmationMessage_Recording_AsksToStop`** — 録音中は停止して終了するかを聞く
- **`CloseConfirmationMessage_Stopping_AsksToWait`** — 停止処理中は完了を待つかを聞く（録音中の文言にしない）
- **`CloseConfirmationMessage_TranscribingFile_AsksToCancel`** — ファイル文字起こし中は中止して終了するかを聞く
- **`CloseConfirmationMessage_StoppingTakesPrecedenceOverRecording`** — 停止処理中は `IsRecording` も true になるため、文言は停止処理中のものを選ぶ

> **テストで守れない範囲:** `Closing` の `e.Cancel` と `MessageBox`、`Close()` の呼び直しは
> WPF のウィンドウが要るためユニットテストで検証できない。`ShutdownAsync` も
> `AudioCaptureService` / `TranscriptionService` の実インスタンスを掴むため単体では動かせない
> （DI 抽象は ADR-0001 で意図的に持たない）。**この 2 つは手動確認のみ。**

## 9. 未解決の質問

なし（D1〜D7 で確定）。

## 10. 前提

- `IsStopping` が true の間は `IsRecording` も true である（`StopRecordingAsync` の実装より）。
- `RunFileTranscriptionAsync` と `StopRecordingAsync` は内部で全例外を握って
  `StatusMessage` へ変換するため、`await` しても例外は飛んでこない。
- ファイル文字起こし中はオプション指定ダイアログがモーダルで開いているため、
  通常の経路ではメインウィンドウの × を押せない。押せるのは
  REQ-TRX-FILE-13 でダイアログを閉じた後だけである（T151 でこの前提は変わる）。

---

## 実行結果 (2026-08-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 200 件成功 / 0 件失敗 / 0 件スキップ（うち T149 追加分 5 件）
- 計画からの逸脱: なし
- 手動確認が要る範囲（§8 のとおり）: 実際の × 押下時の確認ダイアログ・停止待ち・
  「いいえ」でウィンドウが閉じないことは、ユニットテストでは検証していない
