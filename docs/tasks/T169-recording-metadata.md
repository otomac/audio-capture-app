# T169 — 録音のメタデータ（会議名・実施日時・参加者）を作る

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **ADR:** [ADR-0006](../adr/0006-mainviewmodel-split-reevaluation.md)（6 枚目のウィンドウ）

## 1. 目的

録音（と、ファイルからの文字起こし）に会議名・実施日時・参加者を付けて残し、会議名はファイル名でも分かるようにする。

## 2. スコープ境界

**やること**
- 録音停止後のモーダルダイアログ `RecordingMetadataWindow`（REQ-REC-13）。終了確認の経路でも出す
- JSON（`会議名` / `実施日時` / `参加者`）の書き出し（REQ-META-01）
- 会議名によるファイル名の改名（`.mp3` / `.txt`、REQ-META-02）と `LastResultPath` / `RecordingSession.FilePath` の追従
- 「ファイルから文字起こし」ダイアログの 3 項目（REQ-TRX-FILE-18）と `.transcript.txt` の命名（REQ-TRX-FILE-05）
- 参加者の分割（REQ-META-03）とメール形式の整形・並び替え（REQ-META-04、設定 `ParticipantDomainSortedLast` REQ-CFG-11）

**やらないこと（重要）**
- **Google カレンダーからの自動入力は作らない**（T168 でカレンダー案を採らなかった。REQ-META-05）
- **入力ファイル（ファイル文字起こしの元音声）は改名しない**
- **JSON の読み込み・編集 UI は作らない**（書くだけ）
- **改名に失敗しても音声は消さない・動かさない**（JSON だけ書く）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ①キャンセル時 | **JSON を作らず、ファイル名も従来どおり**（既定案） |
| D2 | ②終了確認の経路 | **出す**（既定案）。`ShutdownAsync` → `StopRecordingCoreAsync` の末尾で同期的にイベントを上げ、`MainWindow` が `ShowDialog` する。`ShowDialog` は同期なので、ダイアログを閉じるまで `ShutdownAsync` は完了せず `Close()` に進まない |
| D3 | ③JSON のファイル名 | **音声（改名後）と同名の `.json`**（既定案）。ファイル文字起こしでは `.transcript.txt` と同じ stem |
| D4 | 実施日時の持ち方 | **自由記述の文字列**（既定は `yyyy-MM-dd HH:mm〜HH:mm`）。構造化すると入力欄が 4 つに増え、手で直す用途に合わない |
| D5 | ファイル側の既定値 | 3 項目とも空。**3 項目とも空なら JSON を作らない**（実行のたびに空のメタデータを残さない） |
| D6 | 改名の場所 | `AudioCaptureService.RenameSessionFiles(meetingName)`（セッションのファイルを持つのはここ。ファイル I/O は Service 層）。JSON の書き出しと名前の整形は `Services/RecordingMetadataFile`（static。`TranscriptDiarizationMerger` と同じ形） |
| D7 | 改名先が既にある・失敗 | 改名せず元の名前のまま JSON だけ書き、理由をステータスに出す |
| D8 | 参加者の整形 | メール形式（`@` あり）だけ整形・並び替えの対象。非メールは入力順で先頭。「特定のドメイン」は設定で与える（ソースに書かない） |
| D9 | ダイアログのキャンセル | `IsCancel` の「キャンセル」と × はどちらもキャンセル（何も残さない）。**確認は挟まない** — 残すものが無いため |
| D10 | 自動開始との関係 | ダイアログ表示中は `IsModalDialogOpen`（REQ-REC-12）で自動開始を止める（`MainWindow.ShowModal` を使う） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-REC-04 / 11、**REQ-REC-13 新設**、REQ-OPEN-01、REQ-TRX-FILE-05 / 09、**REQ-TRX-FILE-18 新設**、REQ-CFG-01、**REQ-CFG-11 新設**、**§16 REQ-META-01〜05 新設**
- [x] `docs/spec/02_architecture.md` — View / Model / Service 一覧、`MainViewModel.RecordingMetadata.cs`
- [x] `docs/spec/03_class_diagram.md` — `RecordingMetadata` / `RecordingMetadataFile`、`AudioCaptureService.RenameSessionFiles`、`MainViewModel` の追加メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §4 録音停止にダイアログの流れ

## 5. アーキテクチャへの影響

- ADR: [ADR-0006](../adr/0006-mainviewmodel-split-reevaluation.md)（6 枚目のウィンドウ。暫定承認）。`RecordingMetadataFile` は外部リソースを増やさない（ファイル I/O は既に Service 層にある）static ヘルパーで、Service 間の参照も無い

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Models/RecordingMetadata.cs` | 新規 POCO（JSON のフィールド名は日本語） |
| `AudioCaptureApp/Models/RecordingSession.cs` | `FilePath` を改名に追従できるよう `set` に |
| `AudioCaptureApp/Models/AppSettings.cs` | `ParticipantDomainSortedLast` |
| `AudioCaptureApp/Services/RecordingMetadataFile.cs` | 新規: 名前の整形・参加者の分割と並び替え・JSON 書き出し |
| `AudioCaptureApp/Services/AudioCaptureService.cs` | `RenameSessionFiles` |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `BuildTranscriptPath(audio, meetingName)`、`FileTranscriptionOptions.MeetingName` |
| `AudioCaptureApp/ViewModels/MainViewModel.RecordingMetadata.cs` | 新規 partial |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs` | 停止完了後の要求 |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | 3 項目、完了時の JSON |
| `AudioCaptureApp/RecordingMetadataWindow.xaml(.cs)` | 新規 |
| `AudioCaptureApp/MainWindow.xaml.cs` | 生成・表示 |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | 3 項目 |
| `AudioCaptureApp.Tests/RecordingMetadataFileTests.cs` | 新規 |

## 7. 実装手順

- [x] **A1** Model と `RecordingMetadataFile`（純粋関数）とテスト
- [x] **A2** `AudioCaptureService.RenameSessionFiles`
- [x] **A3** VM partial・停止後の要求・ファイル側の 3 項目
- [x] **A4** ウィンドウと結線
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`SanitizeMeetingName_RemovesInvalidCharsAndTrims`** / **`SanitizeMeetingName_OnlyInvalidOrBlank_ReturnsEmpty`**
- **`WithMeetingName_InsertsBeforeExtension`** / **`WithMeetingName_Empty_ReturnsOriginal`** / **`WithMeetingName_TranscriptTxt_KeepsDoubleExtension`**
- **`BuildMetadataPath_ReplacesExtensionWithJson`**
- **`ParseParticipants_SplitsOnNewlineCommaAndJapaneseComma`**
- **`NormalizeParticipants_EmailsSortedByDomainWithSpecialLast`** / **`NormalizeParticipants_NonEmailsKeepOrderFirst`** / **`NormalizeParticipants_NoSpecialDomain_SortsAllByDomain`**
- **`Write_ProducesJapaneseFieldNamesAndRoundTrips`** — JSON のフィールド名と往復
- **`HeldAtText_SameDay_OmitsEndDate`** / **`HeldAtText_CrossMidnight_IncludesEndDate`** — 実施日時の既定文字列
- **`BuildTranscriptPath_WithMeetingName`**
- **`IsMetadataEmpty_AllBlank_IsTrue`**

> **テストで守れない範囲:** ダイアログの表示、停止処理からの呼び出し順、改名の実行（`File.Move`）。

## 9. 未解決の質問

なし（①②③は既定案）。

## 10. 前提

- 停止処理の完了時点で `.mp3` / `.txt` はどちらも閉じられている（`LameMP3FileWriter.Dispose` と `StopSession` の後）。

---

## 実測（2026-09-22、scratchpad `xamlcheck`。本体の `App` リソースと `MainViewModel` を同一プロセスで動かし、実機マイク Brio 300 で録音）

実体の `settings.json` は退避して差し替え、終了後に復元した（`cmp` で一致を確認）。

| # | 操作 | 結果 |
|---|---|---|
| 1 | 5 枚のウィンドウ（Settings / WhisperModels / FileTranscriptionOptions / RecordingMetadata / LiveTranscript）を生成・表示 | すべて XAML 例外なく描画（サイズ 420×603 / 520×521 / 460×655 / 460×363 / 480×240） |
| 2 | `sample.m4a`（6 秒・無音）をドロップ → ダイアログで会議名「定例/会議」・参加者「z@ours.example, a@alpha.example / 山田」→ 開始 | `sample_定例会議.transcript.txt`（無音なので本文なし）と `sample_定例会議.json` を出力。参加者は `["山田","a","z"]`（`ParticipantDomainSortedLast = ours.example` が最後）。JSON の会議名は入力どおり `定例/会議` |
| 3 | 存在しないモデルを登録して選び「開始」 | `StartFileTranscriptionAsync` が `false`、`FileTranscriptionModelError` に「モデルファイルが見つかりません: …nope.bin」、`IsTranscribingFile = false`（ダイアログは閉じない。T163 の確認も兼ねる） |
| 4 | 録音開始 → 4 秒 → 停止 → メタデータダイアログ（実施日時の既定 `2026-09-22 09:28〜09:28`）で会議名「週次:定例」・参加者 3 名 → OK | `20260922_092813_週次定例.mp3` と `20260922_092813_週次定例.json` に改名・出力。`LastResultPath` は改名後の `.mp3`。参加者 `["田中","b","z"]` |
| 5 | 終了後の `settings.json` | `WhisperModelList` に `ggml-small` が 1 件移行され、`AutoStartRecordingEnabled: false` 等の新キーが既定で書かれた |

- 無音 4 秒の録音ではライブ文字起こしの `.txt` が作られないため、`.txt` 無しの改名経路を通った（`.txt` ありの経路は単体テストで守れず、未実測）。
- UI Automation で実 exe を外から操作する試みは、このサンドボックスではボタンの `Invoke` がアプリに届かず（既存の「表示」ボタンも同様）断念した。

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 313 件成功 / 0 件失敗 / 0 件スキップ（295 → 313、+18）
- 計画からの逸脱: README に録音のメタデータの説明を足した
