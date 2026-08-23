# T150 — ファイル文字起こしの開始時刻を自動入力する

> **状態:** 完了 — 2026-08-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

オプション指定ダイアログの開始時刻（REQ-TRX-FILE-10）は常に空欄で、利用者が毎回手で入れている。
ファイルから推定できる場合は既定値として入れておき、**推定であることを見せたうえで**
そのまま消せるようにする。

## 2. スコープ境界

**やること**
- 対象ファイルが決まった時点で開始時刻を推定し、ダイアログの入力欄へ入れる。
- 推定に使った根拠（ファイル名／作成日時／逆算）をダイアログ上に表示する。
- 利用者が入力欄を編集したら、その表示を消す。

**やらないこと（重要）**
- **入力欄の書式（`h:mm` / `hh:mm`）を変えない。** 秒の入力欄は増やさない。
  したがって自動入力値は**分単位に切り捨てる**（最大 59 秒の誤差は仕様どおり）。
- **空欄＝未指定の扱いを変えない。** 自動入力を消せば従来どおりファイル先頭を `00:00:00` とする。
- **同名の `.txt` / `.transcript.txt` は読まない**（D2）。
- **推定できなかったときにエラーを出さない。** 静かに空欄のままにする。
- **ファイル文字起こしの処理そのもの（`TranscribeFileAsync`）には手を入れない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 取得元と優先順位 | **①ファイル名（`yyyyMMdd_HHmmss`）→ ②ファイルの作成日時 → ③最終更新日時 − 音声の長さ。上から順に試し、最初に取れたものを採用する**（利用者の指示 2026-08-23） |
| D2 | 同名 `.txt` の時刻範囲を使うか | **使わない。**（利用者の指示 2026-08-23）。まだ文字起こししていないファイルには存在しないため、主経路の根拠にならない |
| D3 | ②はいつでも成り立ってしまい③に届かない問題 | **`作成日時 > 最終更新日時` なら②を採らず③へ落とす。** コピー・移動されたファイルは作成日時が「コピーした日時」になり、原本の更新日時より後になる。この逆転が、②が当てにならないことの機械的に検出できる唯一の兆候である。逆転していなければ②を信じる |
| D4 | ①の判定の厳しさ | **拡張子を除いた名前全体が `yyyyMMdd_HHmmss` に一致する場合だけ**採用する。前後に何か付いた名前（`会議_20260823_140000_edited`）は採らない。誤検出して誤った時刻を既定値にするより、空欄のほうが害が小さい |
| D5 | 音声長の読み取り場所 | **`TranscriptionService.TryGetAudioDuration`（`internal static`）。** NAudio を触るのは Service 層の責務（20-architecture-standards.md §1）。ViewModel には純粋な判定だけを置く |
| D6 | ③の読み取りコスト | **③に落ちたときだけ音声長を読む。** `AudioFileReader` は MP3 のフレーム表を作るためにファイル全体を走査するので、常に読むと①②で足りる場合まで待たされる。`Func<TimeSpan?>` で遅延させる |
| D7 | 推定であることの見せ方 | **入力欄の下に根拠を 1 行表示する**（「ファイル名から自動入力しました」等）。入力欄を編集したら消える。読み取り専用にはせず、いつでも消せる |
| D8 | 自動入力値の書式 | **`HH:mm`（24 時間・2 桁）。** `TryParseStartTime` が受理する書式であることをテストで固定する |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — §9 に **REQ-TRX-FILE-15**（開始時刻の自動入力）を新設し、
      REQ-TRX-FILE-10 に「初期値は REQ-TRX-FILE-15 が入れる」ことを追記
- [x] `docs/spec/03_class_diagram.md` — `MainViewModel` に `InferStartTime` /
      `TryParseRecordedFileNameTime`、`TranscriptionService` に `TryGetAudioDuration` を追記
- [ ] `docs/spec/02_architecture.md` — 影響なし（層・依存方向は変わらない）
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし（推定はダイアログ表示要求の直前に閉じた処理）

## 5. アーキテクチャへの影響

- ADR: **不要。** 新しい依存も層も増えない。音声長の読み取りは Service 層に置き、
  ViewModel には純粋関数だけを足す（既存の `BuildTranscriptPath` と同じ形）。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TryGetAudioDuration`（`internal static`）を追加 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `TryParseRecordedFileNameTime` / `InferStartTime` / `StartTimeSource` / `FileTranscriptionStartTimeHint` を追加し、`RequestFileTranscription` から呼ぶ |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | 根拠表示の 1 行を追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 推定ロジックのテストを追加 |
| `docs/spec/01_requirements.md` | REQ-TRX-FILE-15 を追加、REQ-TRX-FILE-10 を追記 |
| `docs/spec/03_class_diagram.md` | 追加メソッドを反映 |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** §9 に REQ-TRX-FILE-15 を追加し REQ-TRX-FILE-10 を追記 (`01_requirements.md`)
- [x] **A2** クラス図へ反映 (`03_class_diagram.md`)

### グループ B — Service
- [x] **B1** `TryGetAudioDuration` を追加 (`TranscriptionService.cs`)

### グループ C — ViewModel
- [x] **C1** `TryParseRecordedFileNameTime` を追加
- [x] **C2** `StartTimeSource` と `InferStartTime` を追加
- [x] **C3** `FileTranscriptionStartTimeHint` を追加し、入力欄の変更で消す
- [x] **C4** `RequestFileTranscription` から推定を呼ぶ

### グループ D — View
- [x] **D1** 根拠表示の行を追加 (`FileTranscriptionOptionsWindow.xaml`)

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`TryParseRecordedFileNameTime_RecordedName_Parses`** — `20260823_140530.mp3` から 14:05:30 を取る
- **`TryParseRecordedFileNameTime_NameWithSuffix_Rejects`** — 前後に何か付いた名前は採らない（D4）
- **`TryParseRecordedFileNameTime_InvalidDate_Rejects`** — `20261332_140530` のような不正な日付を弾く
- **`InferStartTime_RecordedFileName_UsesFileName`** — ①が取れれば②③を見ない
- **`NeverCalled()` ヘルパー** — ①②で決まる経路では音声長プロバイダーを呼ばない（D6）。呼ばれたら例外で落ちるため、テスト側で遅延読み取りを保証している
- **`InferStartTime_PlainName_UsesCreationTime`** — ①が無く作成日時が逆転していなければ②
- **`InferStartTime_CopiedFile_UsesLastWriteMinusDuration`** — 作成日時 > 更新日時なら③（D3）
- **`InferStartTime_CopiedFileWithoutDuration_ReturnsEmpty`** — ③の材料も無ければ空欄
- **`InferStartTime_LastWriteMinusDurationCrossesMidnight_WrapsToPreviousDay`** — 日付をまたいでも時刻だけを採る
- **`InferStartTime_FullPath_UsesFileNamePart`** — 呼び出し側が渡すフルパスでも①が効く
- **`InferStartTime_NoCreationTime_FallsBackToLastWriteMinusDuration`** — 作成日時だけ取れない場合も③で救う
- **`StartTimeHintFor_None_IsEmpty`** — 推定していないときに注意書きだけ残らない
- **`InferStartTime_NoFileTimes_ReturnsEmpty`** — 日時が取れない場合は空欄
- **`InferStartTime_Result_IsAcceptedByTryParseStartTime`** — **自動入力した文字列は必ず入力欄の書式として受理される**（D8）

> **テストで守れない範囲:** `File.GetCreationTime` / `AudioFileReader` の実挙動（コピーで作成日時が
> 書き換わること、MP3 の長さ取得）は実ファイルが要るため検証しない。**純粋関数への入力として渡す**
> 形にしてあるので、テストが守るのは「与えられた材料からどう決めるか」までである。

## 9. 未解決の質問

なし（D1〜D8 で確定。取得元の優先順位と `.txt` 不使用は利用者の指示による）。

## 10. 前提

- 本アプリの録音ファイル名は `yyyyMMdd_HHmmss.mp3`（REQ-REC-04）。
- コピー・移動されたファイルは作成日時が最終更新日時より後になる（D3 の根拠）。
  ダウンロード後に編集した等、逆転しないコピーは検出できない。
- `AudioFileReader` は `.wav` / `.mp3` を開ける（REQ-TRX-FILE-01 の対応拡張子と一致）。

---

## 実行結果 (2026-08-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 210 件成功 / 0 件失敗 / 0 件スキップ（うち T150 追加分 13 件）
- 計画からの逸脱: なし
- 手動確認が要る範囲: 実ファイルでの作成日時・音声長の取得（§8 のとおりテストしていない）
