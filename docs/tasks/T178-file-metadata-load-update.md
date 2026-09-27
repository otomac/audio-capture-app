# T178 — ファイル文字起こしで既存のメタデータ JSON を読み込み・更新する

> **状態:** 進行中 — 2026-09-27（実装済み・品質ゲート未実行）
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

録音時に作ったメタデータ（T169）を、その音声を後からファイル文字起こしするときに入力し直さずに済むようにし、編集した内容を同じ JSON に反映する。

## 2. スコープ境界

**やること**
- ダイアログを開くとき、入力ファイルと同じ stem の `.json` を読み、3 項目の初期値にする（選択・ドロップのどちらの経路でも）
- 完了時、書き出し先の `.json` が既にあれば 3 項目のキーだけを更新する（他のキーは残す）
- 改名済みの録音に同じ会議名を二重に付けない（`WithMeetingName` を冪等に）

**やらないこと（重要）**
- **録音停止時の JSON（REQ-REC-13）は従来どおり上書き**（新しく作るファイルなので残すべき内容が無い）
- **会議名を変えて文字起こししたとき、読み込み元の JSON は更新しない**（書き出し先は `.transcript.txt` と同じ stem の `.json` のまま。REQ-META-01 の命名を変えない）
- **ダイアログに「読み込んだ」旨の表示は足さない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 読み込み元 | `BuildMetadataPath(入力ファイル)`（`a.mp3` → `a.json`）。録音時の JSON と同じ規則 |
| D2 | 読めない・不正な JSON | **黙って空にする**（従来の挙動に倒す） |
| D3 | 参加者の戻し方 | 1 行に 1 人（`Environment.NewLine` 区切り）。`ParseParticipants` で往復できる |
| D4 | 「更新」の意味 | **3 項目のキーだけを書き換え、他のキーは位置も含めて残す**（`JsonNode` で読み書き）。利用者が手で書き足した項目を失わないため |
| D5 | 既存の JSON が壊れているとき | **書き換えない**。`JsonException` をステータスバーの「メタデータの保存に失敗しました」に出す |
| D6 | 3 項目とも空で、書き出し先が既にある | **更新する**（利用者が空にした、という意思を反映する）。無ければ従来どおり作らない |
| D7 | 二重の会議名 | 元の stem が既に `_会議名`（整形後）で終わっていれば付けない。録音の改名（`RenameSessionFiles`）でも同じ関数を通るが、録音の stem は `yyyyMMdd_HHmmss` なので影響しない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-05 / 18 改訂、**REQ-TRX-FILE-19 新設**、REQ-META-01 / 02 改訂
- [x] `docs/spec/02_architecture.md` — データ保存先の表
- [x] `docs/spec/03_class_diagram.md` — `RecordingMetadataFile.TryRead` / `Update`
- [x] `docs/spec/04_sequence_diagram.md` — §6 に読み込みと更新

## 5. アーキテクチャへの影響

- ADR: 不要（既存の static ヘルパー `RecordingMetadataFile` にメソッドを足すだけ。`System.Text.Json.Nodes` は BCL で、パッケージは増えない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/RecordingMetadataFile.cs` | `TryRead` / `Update` / `FormatParticipants` 追加、`WithMeetingName` を冪等に |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `RequestFileTranscription` で読み込み、`WriteFileTranscriptionMetadata` で更新 |
| `AudioCaptureApp.Tests/RecordingMetadataFileTests.cs` | テスト追加 |

## 7. テスト一覧

- **`WithMeetingName_AlreadySuffixed_ReturnsOriginal`** — 二重に付けない
- **`BuildTranscriptPath_RenamedRecording_SharesMetadataPathWithAudio`** — 改名済みの録音では読み込み元と書き出し先の JSON が一致する
- **`TryRead_ExistingFile_ReturnsMetadata`** / **`TryRead_MissingOrInvalid_ReturnsNull`** / **`TryRead_MissingFields_AreEmpty`**
- **`FormatParticipants_OnePerLineAndRoundTripsThroughParse`**
- **`Update_ReplacesMetadataKeysAndKeepsOthers`** — 他のキーを残す
- **`Update_InvalidExisting_ThrowsAndLeavesFileUnchanged`** — 壊れた JSON は書き換えない

> **テストで守れない範囲:** ダイアログへの反映（`RequestFileTranscription` は `WhisperModels` 等の状態に依存する）。

## 実行結果

- 未実行。作業環境（Linux コンテナ）に .NET SDK が無く、`builds.dotnet.microsoft.com` への接続もネットワーク制限で拒否された。Windows で G1〜G3 を実行して記録すること
