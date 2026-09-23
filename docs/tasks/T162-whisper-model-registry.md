# T162 — Whisper モデルにエイリアス名を付けて登録し、ドロップダウンで選べるようにする

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **ADR:** [ADR-0006](../adr/0006-mainviewmodel-split-reevaluation.md)（5 枚目のウィンドウ）

## 1. 目的

モデルを切り替えるたびにファイル選択ダイアログでパスを探す手間をなくす。複数のモデル（small / large 等）を
名前で登録しておき、ドロップダウンで選べるようにする。T163（ファイル文字起こし側のモデル選択）の土台でもある。

## 2. スコープ境界

**やること**
- `AppSettings.WhisperModelList`（`WhisperModelEntry` のリスト）と読み込み時の移行（REQ-CFG-08）
- 設定ウィンドウ: パス表示＋「選択」 → エイリアスのドロップダウン＋「モデル管理…」（REQ-SETWIN-03 ②、REQ-MODELWIN-07）
- モデル管理ダイアログ `WhisperModelsWindow`（追加・名前変更・削除、重複の検証。REQ-MODELWIN-01〜06）
- `TranscriptionService.UnloadModel`（選択解除でモデルを破棄する）

**やらないこと（重要）**
- **ファイル文字起こし側のモデル選択は作らない**（T163）
- **登録時の読み込み検証はしない**（D3）
- **`WhisperModelPath` の意味は変えない**（選択中モデルの実パス。D1）
- **`SelectWhisperModelCommand`（パスの直接選択）は削除する** — 置き換えであり、2 経路を残さない

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ライブ側の選択の持ち方 | **`WhisperModelPath` に選択中の実パスを書き続ける**（起票時の既定案 (a)。旧バージョン互換、REQ-CFG-04 の既定値も生きる） |
| D2 | 選択中のモデルを削除したとき | **選択を解除して「モデル未設定」に倒す**（既定案 (b)）。`UnloadModel` で読み込み済みモデルも破棄する — 破棄しないと `IsModelLoaded` が true のまま「未設定」と表示され、ファイル文字起こしが古いモデルで動く |
| D3 | 登録時の読み込み検証 | **しない**（既定案 (c)）。存在確認だけ |
| D4 | 移行規則 | 一覧が無く `WhisperModelPath` だけ → 1 件へ移行（名前はファイル名、拡張子なし）。一覧はあるが `WhisperModelPath` がどれとも一致しない（手編集）→ 同じく 1 件を足す。**`WhisperModelPath` が空なら何も足さない** |
| D5 | 名前の重複時の移行 | 移行で足す名前が既存と重複したら `名前 (2)` のように連番を付ける（起動時に検証エラーで止まれないため） |
| D6 | ダイアログの生成元 | `SettingsWindow` のコードビハインド（モーダルの上に出すため。ADR-0006 規則 4） |
| D7 | 一覧の表示更新 | `WhisperModelEntry` は POCO のまま（`INotifyPropertyChanged` を実装しない）。名前変更は**同じ位置へ新しいインスタンスを差し替える**ことで `ObservableCollection` に通知させる |
| D8 | 「参照…」の既定名 | 名前欄が空のときだけファイル名（拡張子なし）を入れる。入力済みの名前は上書きしない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-CFG-01 / 04 の改訂、**REQ-CFG-08 新設**、REQ-SETWIN-03 ②の改訂、**§15 REQ-MODELWIN-01〜07 新設**
- [x] `docs/spec/02_architecture.md` — View 一覧・図・`MainViewModel.WhisperModels.cs`・Model 一覧
- [x] `docs/spec/03_class_diagram.md` — `WhisperModelEntry`、`AppSettings.WhisperModelList`、`MainViewModel` の追加メンバー、`TranscriptionService.UnloadModel`
- [x] `docs/spec/04_sequence_diagram.md` — §1 起動時の一覧復元

## 5. アーキテクチャへの影響

- ADR: **要** → [ADR-0006](../adr/0006-mainviewmodel-split-reevaluation.md)（5 枚目のウィンドウ。案 D 継続、暫定承認）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Models/WhisperModelEntry.cs` | 新規 POCO |
| `AudioCaptureApp/Models/AppSettings.cs` | `WhisperModelList` |
| `AudioCaptureApp/ViewModels/MainViewModel.WhisperModels.cs` | 新規 partial: 一覧・選択・編集状態・検証・移行・コマンド |
| `AudioCaptureApp/ViewModels/MainViewModel.Transcription.cs` | `SelectWhisperModelCommand` を削除 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 起動時の移行と選択の復元、`SaveSettings`、`NotifyCanExecuteChangedFor` の付け替え |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `UnloadModel` |
| `AudioCaptureApp/SettingsWindow.xaml(.cs)` | ドロップダウン＋「モデル管理…」、ダイアログの生成 |
| `AudioCaptureApp/WhisperModelsWindow.xaml(.cs)` | 新規 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 移行・検証のテスト |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | `WhisperModelList` の往復 |

## 7. 実装手順

- [x] **A1** `WhisperModelEntry` と `AppSettings.WhisperModelList`
- [x] **A2** `MigrateWhisperModelList` / `ValidateWhisperModelEntry`（純粋関数）とテスト
- [x] **A3** `MainViewModel.WhisperModels.cs`（一覧・選択・編集・コマンド・イベント）
- [x] **A4** `TranscriptionService.UnloadModel`
- [x] **A5** `SettingsWindow` の差し替えと `WhisperModelsWindow`
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`MigrateWhisperModelList_NoListWithPath_CreatesOneEntryNamedByFileName`** — 互換移行
- **`MigrateWhisperModelList_NoListNoPath_ReturnsEmpty`** — 空のまま
- **`MigrateWhisperModelList_ListContainsPath_IsUnchanged`** — 一致する要素があれば触らない（大文字小文字を区別しない）
- **`MigrateWhisperModelList_ListWithoutPath_AppendsEntry`** — 手編集で一致しないときは 1 件足す
- **`MigrateWhisperModelList_DuplicateName_AppendsSuffix`** — 名前が重複したら連番
- **`ValidateWhisperModelEntry_BlankName_ReturnsError`**
- **`ValidateWhisperModelEntry_MissingFile_ReturnsError`**
- **`ValidateWhisperModelEntry_DuplicateName_ReturnsError`**
- **`ValidateWhisperModelEntry_DuplicatePathIgnoringCase_ReturnsError`**
- **`ValidateWhisperModelEntry_Rename_IgnoresSelfForDuplicates`** — 名前変更では自分自身を重複と数えない
- **`ValidateWhisperModelEntry_Valid_ReturnsNull`**
- **`JsonRoundTrip_PreservesWhisperModelList`** — 設定の往復

> **テストで守れない範囲:** ダイアログの表示・選択とモデル読み込みの結線（`MainViewModel` は単体テスト不可）。

## 9. 未解決の質問

なし（D1〜D8 で確定。起票時の (a)(b)(c) はいずれも既定案を採った）。

## 10. 前提

- `TranscriptionService.LoadModel` は呼ぶたびに既存の factory を破棄してから読み込む（`DisposeProcessor()` を先頭で呼ぶ）。`UnloadModel` はその破棄だけを公開する。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 280 件成功 / 0 件失敗 / 0 件スキップ（268 → 280、+12）
- 計画からの逸脱: `AppSettings.WhisperModelList` は `List<T>` の setter 付きではなく **`Collection<T>` の読み取り専用プロパティ**にした（CA2227 / CA1002）。System.Text.Json が setter 無しのコレクションを読み飛ばすため `[JsonObjectCreationHandling(Populate)]` を付け、往復テストで確認した。README にモデル登録の導線を 1 行足した
