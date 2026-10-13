# T185 — 確定パスに絶対位置・確定完了の通知・計測ログを足す

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **設計:** [T181](./T181-two-pass-live-transcription-design.md) D4 / D5 / D16、[ADR-0007](../adr/0007-two-pass-live-transcription.md)（承認済み）

## 1. 目的

速報（T186）と確定を対応づける土台を作る。確定パスのチャンクに音源ごとの絶対サンプル位置を持たせ、
チャンクを処理し終えるたびに「音源・終端位置・セッション ID」を通知する（REQ-TRX-LIVE-18）。
あわせて、実アプリで速報・確定の所要時間を確かめられるよう、数値だけの計測ログを隠し設定で書けるようにする（REQ-CFG-12）。

## 2. スコープ境界

**やること**
- `PendingChunk` に絶対位置（`StartPosition`）、`SourceState` にバッファ先頭の絶対位置（`BufferStartPosition`）と音源種別を持たせる
- セッション ID（`CurrentSessionId`）を `StartSession` で採番する
- `LiveChunkCompleted` イベントを足し、`ProcessChunk` の最後で必ず発火する（0 行・キャンセル・例外でも）
- 計測ログ `TranscriptionMetricsLog`（新規）と設定 `TranscriptionMetricsLogEnabled`

**やらないこと（重要）**
- **確定パスの区切り方・出力（`.txt` の書式・追記のタイミング）・時刻の求め方は 1 つも変えない**
- **画面は変えない**（`LiveChunkCompleted` を読むのは T187）
- **速報（T186）・文脈プロンプト（T188）には触れない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 絶対位置の単位 | **音源ごとの 16kHz 換算の累計サンプル数**（`long`）。セッション開始で 0。供給の途切れ（ギャップ分割）では進まない — 時刻ではなく「その音源に届いた音声の何サンプル目か」を表す |
| D2 | 位置の持ち方 | `SourceState.BufferStartPosition` は `Pcm16kBuffer[0]` の絶対位置。バッファから取り出す（`TakeNextChunk`・ギャップ分割・停止時の残り）たびに取り出した長さだけ進める。チャンクの終端は `StartPosition + Samples.Length` |
| D3 | 通知の型 | `LiveChunkCompletion(long SessionId, AudioSourceType Source, long EndPosition)`（`readonly record struct`）。`Action<LiveChunkCompletion>` |
| D4 | 通知の発火場所 | `ProcessChunk` の最後（`.txt` への追記の後ろ）。`try` の外に置くので、キャンセル・例外で区間ループを抜けても必ず通る |
| D5 | 計測ログの置き場 | **`%APPDATA%\AudioCaptureApp\logs\metrics_yyyyMMdd_HHmmss.tsv`**（録音開始時刻）。録音フォルダには置かない — 停止後の改名（REQ-META-02）は `.mp3` / `.txt` しか動かさず、ログだけが旧名で取り残されるため |
| D6 | 計測ログの中身 | Whisper の呼び出し 1 回につき 1 行。列は `t_s`（セッション開始からの秒）・`pass`（`final` / `provisional`）・`source`・`start_s` / `audio_s`（絶対位置を秒にしたもの）・`call_ms`・`segments`・`behind`（確定のみ）・`pending_s`・`interval_s`（速報のみ）。**本文は書かない** |
| D7 | 計測ログの失敗 | 開けない・書けないときは記録を止めて `Error` で 1 度だけ知らせる。文字起こしは続ける（計測のためにセッションを落とさない） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-18 の実装箇所、REQ-CFG-12 にキー名を書き（`TranscriptionMetricsLogEnabled` ほか）、計測ログの置き場と列を書く
- [x] `docs/spec/02_architecture.md` — §6 永続化に計測ログを追加
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService` の `LiveChunkCompleted` / `CurrentSessionId` / `MetricsLogEnabled`、`LiveChunkCompletion`、`TranscriptionMetricsLog`、`AppSettings` のキー
- [x] `docs/spec/04_sequence_diagram.md` — 変更なし（処理順序は変えない。通知が 1 つ増えるだけで、図は T186 / T187 でまとめて直す）

## 5. アーキテクチャへの影響

- ADR: 不要 — ADR-0007（承認済み）の決定 2 をそのまま実装する。新しいスレッドは無い。Service → ViewModel の通知はイベント（既存の `SegmentTranscribed` と同じ形）。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `partial` にする。位置・セッション ID・`LiveChunkCompleted`・計測ログの呼び出し |
| `AudioCaptureApp/Services/TranscriptionMetricsLog.cs` | 新規。スレッド安全な TSV 書き出し |
| `AudioCaptureApp/Models/AppSettings.cs` | `TranscriptionMetricsLogEnabled` |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 設定を `TranscriptionService.MetricsLogEnabled` へ写す |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 位置のテスト |
| `AudioCaptureApp.Tests/TranscriptionMetricsLogTests.cs` | 新規 |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値 |

## 7. 実装手順

### グループ A — 位置とセッション ID
- [x] **A1** `PendingChunk` に `StartPosition`、`SourceState` に `BufferStartPosition` / `Type` を足す
- [x] **A2** `TakeNextChunk`・ギャップ分割（`SplitAtChunkLimit`）・停止時の残りで位置を進める
- [x] **A3** `StartSession` でセッション ID を採番し、位置を 0 に戻す

### グループ B — 通知と計測ログ
- [x] **B1** `LiveChunkCompleted` を `ProcessChunk` の最後で発火する
- [x] **B2** `TranscriptionMetricsLog` を作り、確定の呼び出しごとに 1 行書く
- [x] **B3** 設定キーと `MainViewModel` での写し

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`TakeNextChunk_AdvancesBufferStartPosition`** — 取り出したチャンクの `StartPosition` が直前の終端と連続し、`BufferStartPosition` が取り出した長さだけ進む
- **`SplitAtChunkLimit_AssignsContiguousPositions`** — ギャップ分割で 20 秒ずつに分けたチャンクの位置が連続する
- **`ChunkEndPosition_IsStartPlusLength`** — 終端の求め方
- **`TranscriptionMetricsLog_WritesHeaderAndRows_WithoutText`** — ヘッダーと数値の行だけを書く（不変カルチャ）
- **`TranscriptionMetricsLog_OpenFailure_DisablesAndReportsOnce`** — 開けないときは書かずに 1 度だけ理由を返す
- **`DefaultValues_AreCorrect`**（改訂） — `TranscriptionMetricsLogEnabled` の既定は `false`

> **テストで守れない範囲:** 実際の Whisper 呼び出しでキャンセル・例外が起きたときに通知が出ること（モデルが要る）。
> `ProcessChunk` の構造（`try` の外で発火）で担保し、T187 の実機確認で見る。

## 9. 未解決の質問

なし（D5 の置き場は「録音フォルダに置くと改名で取り残される」ため既定案で決めた）。

## 10. 前提

- `StartSession` の前に `RegisterSource` が呼ばれる（今と同じ）。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 404 件成功 / 0 件失敗 / 0 件スキップ（着手前 396 件 + 本タスク 8 件）
- 計画からの逸脱: テストを 1 件足した（`TakeNextChunk_ReadyChunk_KeepsItsOwnPosition` — ギャップ分割で確定済みのチャンクが自分の位置を保つこと）。`TranscriptionMetricsLog_WriteAfterDispose_IsIgnored` / `PathFor_UsesSessionStartTimeInAppDataLogs` も足した
- 未確認: 実際の Whisper 呼び出しでキャンセル・例外になったときの通知（モデルが要る）。構造で担保し、T187 の実機確認で見る
