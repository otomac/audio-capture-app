# T167 — 「ファイルから文字起こし」ダイアログに話者人数のドロップダウンを置く

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **前提:** [T147](./T147-known-speaker-count-in-dialog.md) は同じ UI を実測のうえ取り下げている。
> 本タスクは精度改善ではなく、**利用者が自分の音声で試せる手段**を用意するもの（利用者の決定 2026-09-22）。

## 1. 目的

`KnownSpeakerCount` を `settings.json` の手編集なしに、実行ごとにダイアログから試せるようにする。
**既定値（設定 `null`・「指定なし」）で結果が変わってはならない。**

## 2. スコープ境界

**やること**
- ドロップダウン「指定なし」「1 人」〜「9 人」「10 人以上」（REQ-TRX-DIA-17）
- 選択値を `SpeakerDiarizationService.Diarize` へ渡し、実行ごとに `SetConfig` でクラスタリング設定を差し替える（REQ-TRX-DIA-10）
- 「人数を指定すると結果が悪くなることがある（T147）」の 1 行をダイアログに置く
- `TranscribeFileAsync` の引数を `FileTranscriptionOptions` レコードにまとめる（開始時刻・言語・話者人数）

**やらないこと（重要）**
- **既定値・クラスタリング閾値・モデルの扱いは変えない**
- **選択を `settings.json` に覚えない**
- **`SpeakerDiarizationOptions` のコンストラクターの意味は変えない**（設定値は従来どおりそこで固定し、実行時の上書きは `Diarize` の引数で受ける）
- **精度の再実測はしない**（T147 の実測が根拠であり、本タスクは手段の提供）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 「指定なし」「10 人以上」の意味 | **どちらも「未選択」** = 設定値 `KnownSpeakerCount` へ倒す（起票時の「ダイアログの選択を優先し、未選択なら設定値」）。設定が 12 のような値でも従来どおりその値が効く（既定の挙動を変えないため） |
| D2 | ダイアログの既定 | 設定値が 1〜9 ならその人数、それ以外は「指定なし」 |
| D3 | 値の受け渡し | `TranscribeFileAsync(string, FileTranscriptionOptions, ...)` に改める。引数が 6 個に達しており、T163 でモデルも足すため、レコードへまとめる |
| D4 | `SetConfig` の適用 | `Diarize` の中で「今適用している話者数」と比べ、違うときだけ `SetConfig`。同じなら呼ばない（REQ-TRX-DIA-10） |
| D5 | 有害さの注記 | ドロップダウンの下に 1 行。「人数を指定すると結果が悪くなることがあります（実測 T147）」 |
| D6 | チェック OFF 時 | ドロップダウンを無効化（起票どおり）。処理中も無効 |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-09、REQ-TRX-DIA-07（優先順位）、REQ-TRX-DIA-10（`SetConfig`）、**REQ-TRX-DIA-17 新設**
- [x] `docs/spec/03_class_diagram.md` — `FileTranscriptionOptions` / `SpeakerCountOption`、`Diarize` と `TranscribeFileAsync` のシグネチャ、`MainViewModel` のプロパティ
- [x] `docs/spec/04_sequence_diagram.md` — `TranscribeFileAsync` / `Diarize` の引数
- 変更なし: `02_architecture.md`

## 5. アーキテクチャへの影響

- ADR: **不要**。sherpa-onnx への依存は引き続き `SpeakerDiarizationService` の中に閉じる。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/SpeakerDiarizationService.cs` | `SpeakerCountOption` / `SpeakerCountOptions`、`Diarize(samples, knownSpeakerCount, ...)`、`EffectiveSpeakerCount`、`SetConfig` の適用 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `FileTranscriptionOptions` レコード、`TranscribeFileAsync` のシグネチャ |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `SpeakerCountOptions` / `SelectedSpeakerCount` / `SpeakerCountOptionFor`、開くたびの既定、`FileTranscriptionOptions` の組み立て |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | ドロップダウンと注記 |
| `AudioCaptureApp.Tests/SpeakerDiarizationServiceTests.cs` | `EffectiveSpeakerCount` / 選択肢のテスト |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `SpeakerCountOptionFor` のテスト |

## 7. 実装手順

- [x] **A1** `SpeakerCountOption` と選択肢一覧、`EffectiveSpeakerCount`
- [x] **A2** `Diarize` に人数の引数を足し、`SetConfig` を差分適用
- [x] **A3** `FileTranscriptionOptions` レコードと `TranscribeFileAsync` の改修
- [x] **A4** ViewModel と XAML
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`SpeakerCountOptions_All_HasUnspecifiedOneToNineAndTenPlus`** — 選択肢が 11 個で、両端が `null`
- **`EffectiveSpeakerCount_DialogSelected_OverridesSettings`** — ダイアログの 1〜9 が設定値より優先
- **`EffectiveSpeakerCount_Unselected_FallsBackToSettings`** — 未選択なら設定値（`null` なら `null`）
- **`SpeakerCountOptionFor_SettingsOneToNine_SelectsThatCount`** — 既定は設定値の人数
- **`SpeakerCountOptionFor_NullZeroOrTenPlus_SelectsUnspecified`** — それ以外は「指定なし」

> **テストで守れない範囲:** `SetConfig` の実行（ネイティブ）。T147 §7-4 の実測に依る。

## 9. 未解決の質問

なし。

## 10. 前提

- `OfflineSpeakerDiarization.SetConfig(OfflineSpeakerDiarizationConfig)` が 1.13.5 に存在する（アセンブリのリフレクションで確認済み）。
- `SetConfig` はクラスタリング設定だけを差し替え、モデルを読み込み直さない（T147 §7-4 の実測）。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 268 件成功 / 0 件失敗 / 0 件スキップ（257 → 268、+11）
- 計画からの逸脱: README の `KnownSpeakerCount` の行にダイアログから試せる旨を 1 文足した（利用者向け説明の追随。仕様の正ではない）
