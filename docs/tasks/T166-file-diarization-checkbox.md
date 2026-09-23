# T166 — 「ファイルから文字起こし」ダイアログに「話者識別を行う」チェックボックスを置く

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

話者識別を有効にしていても、1 回きりの文字起こしでは通さずに済ませたい場面がある
（速さを優先したい・話者欄が要らない）。実行ごとに ON/OFF を選べるようにする。

## 2. スコープ境界

**やること**
- オプション指定ダイアログ（REQ-TRX-FILE-09）に「話者識別を行う」チェックボックスを置く
- 操作できるのは REQ-TRX-DIA-15 の状態が「①有効」のときだけ。②モデル未配置・③無効では OFF 固定
- OFF なら `TranscribeFileAsync` に `null` を渡す（新要件 REQ-TRX-DIA-16）

**やらないこと（重要）**
- **`TranscriptionService` / `SpeakerDiarizationService` は変えない**（切り替えは既に `null` か否かで決まっている）
- **選択を `settings.json` に覚えない**（D1）
- **メインウィンドウ・設定ウィンドウは変えない**（状態表示はステータスバーのまま）
- **話者人数のドロップダウンは置かない**（T167）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 選択を `settings.json` に覚えるか | **覚えない。** 1 回きりの選択であり、覚えると「有効なのに勝手に OFF」が起きる（起票時の既定案） |
| D2 | 既定値 | **①有効なら ON**（従来と同じ挙動）。②③は OFF 固定 |
| D3 | 「変更不可」の根拠 | 起動時に判定済みの `DiarizationAvailability` を `_diarizationAvailability` として保持し、`IsDiarizationSelectable` で判定する。起動後にモデルを置いても再起動まで反映されない（REQ-TRX-DIA-15 の「起動時に 1 度だけ」と同じ） |
| D4 | ②モデル未配置の挙動変更 | 従来は実行時に REQ-TRX-DIA-11 のエラーで中止していたが、OFF 固定になるため**話者欄なしで完走する**。仕様（REQ-TRX-DIA-16）に明記した。エラーで止まるより実害が小さく、起票時の「OFF で変更不可」に従う |
| D5 | 押せない理由の表示 | チェックボックスの下に `SpeakerDiarizationStatus`（REQ-TRX-DIA-15 の文言）をそのまま出す。文言を 2 か所に持たない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-09（ダイアログ項目）、REQ-TRX-DIA-03（UI からの切り替えの位置づけ）、REQ-TRX-DIA-15（役割分担）、**REQ-TRX-DIA-16 新設**
- [x] `docs/spec/03_class_diagram.md` — `FileDiarizationEnabled` / `CanChooseFileDiarization` / `IsDiarizationSelectable`
- 変更なし: `02_architecture.md` / `04_sequence_diagram.md`（呼び出し順序は変わらない。渡す引数が `null` になりうるだけ）

## 5. アーキテクチャへの影響

- ADR: **不要**。ViewModel のプロパティ追加と XAML の 1 項目。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.Transcription.cs` | `IsDiarizationSelectable`、`_diarizationAvailability`、`CanChooseFileDiarization` |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | コンストラクターで状態を保持 |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `FileDiarizationEnabled`、ダイアログを開くときの既定値、`null` の渡し分け |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | チェックボックスと状態の 1 行 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `IsDiarizationSelectable` のテスト |

## 7. 実装手順

- [x] **A1** `IsDiarizationSelectable(DiarizationAvailability)` を追加し、コンストラクターで状態を保持する
- [x] **A2** `FileDiarizationEnabled` を追加。`RequestFileTranscription` で既定値を入れる
- [x] **A3** `RunFileTranscriptionAsync` で OFF なら `null` を渡す
- [x] **A4** XAML にチェックボックスと状態の 1 行を置く
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`IsDiarizationSelectable_Available_ReturnsTrue`** — ①有効のときだけ選べる
- **`IsDiarizationSelectable_ModelMissingOrDisabled_ReturnsFalse`** — ②③は選べない（OFF 固定）

> **テストで守れない範囲:** ダイアログ上の実際の有効／無効表示、`null` が渡ることの結線。
> `MainViewModel` はコンストラクターでデバイスと Whisper に触るため単体テストできない（既存の制約）。

## 9. 未解決の質問

なし（D1〜D5 で確定）。

## 10. 前提

- `TranscribeFileAsync` は `diarization == null` を「無効時」と同じに扱う（`TranscriptionService.cs` の分岐を読んで確認済み）。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 257 件成功 / 0 件失敗 / 0 件スキップ（255 → 257、+2）
- 計画からの逸脱: なし
