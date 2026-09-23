# T161 — 「音声ファイルから文字起こし」の対応形式に M4A（AAC）を加える

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

会議録音を M4A（AAC）で受け取ることがあるため、変換せずにそのまま「音声ファイルから文字起こし」へ
渡せるようにする。

## 2. スコープ境界

**やること**
- ファイル選択ダイアログのフィルターとドロップ時の拡張子判定に `.m4a` を加える（REQ-TRX-FILE-01 / 02 / 03）
- 音声ファイルを開けなかったときの失敗メッセージに形式（拡張子）を含める（REQ-TRX-FILE-03）
- 実ファイルでデコード・`TotalTime`・開始時刻推定の経路が通ることを実測する

**やらないこと（重要）**
- **追加ライブラリ・テンポラリ WAV 変換は入れない**（実測で読めたため。起票時の既定案どおり）
- **デコード本体（`AudioFileReader` を使う 3 か所）の構造は変えない**。開く箇所を 1 つのヘルパーへ寄せるだけ
- **T134（失敗理由がステータスバーに残らない）は直さない**。形式名は `Error` イベントのメッセージに入れるところまで

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | デコード手段 | **NAudio `AudioFileReader` のまま**（`.wav` 以外は Media Foundation へフォールバックする）。実測で読めた（§実行結果） |
| D2 | 失敗メッセージ | `AudioFileReader` を開く 3 か所を `OpenAudioFile(path)` に寄せ、失敗時は `InvalidOperationException("音声ファイルを開けませんでした (形式: .m4a): <原因>")` に包む。`.m4a` のときだけ N エディション（Media Feature Pack）の注記を添える |
| D3 | VBR AAC | **未検証のまま出す。** 手元に VBR AAC を作る手段が無い（Media Foundation のエンコーダーは CBR）。仕様に「未検証」と明記した |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-01（フィルター）/ 02（拡張子）/ 03（形式名を含む失敗メッセージ、実測値、N エディションの注記）
- 変更なし: `02_architecture.md` / `03_class_diagram.md`（新設するのは private 相当の内部ヘルパーのみ）/ `04_sequence_diagram.md`

## 5. アーキテクチャへの影響

- ADR: **不要**。層構成・依存方向・ライブラリに変更なし。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | フィルターと `IsSupportedAudioExtension` に `.m4a` を追加 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `OpenAudioFile` を新設し、`TranscribeFileCoreAsync` / `DecodeToMono16k` / `TryGetAudioDuration` から使う |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 拡張子判定のテスト |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 失敗メッセージに形式が含まれることのテスト |

## 7. 実装手順

### グループ A — 入口
- [x] **A1** `OpenFileDialog` のフィルターに `*.m4a` を足す
- [x] **A2** `IsSupportedAudioExtension` に `.m4a` を足す

### グループ B — 失敗メッセージ
- [x] **B1** `TranscriptionService.OpenAudioFile` を追加し、3 か所から呼ぶ

### グループ Z — 検証
- [x] **Z1** `dotnet build` — 警告 0 件
- [x] **Z2** `dotnet format --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test` — 全件成功
- [x] **Z4** 仕様書の読み直し

## 8. テスト一覧

- **`IsSupportedAudioExtension_SupportedFormats_ReturnsTrue`** — `.wav` / `.mp3` / `.m4a` / `.M4A` を受理する
- **`IsSupportedAudioExtension_Unsupported_ReturnsFalse`** — `.aac` / `.flac` / `.mp4` / 拡張子なし は拒否する
- **`OpenFailureMessage_M4a_ContainsFormatAndMediaFeaturePackHint`** — `.m4a` の失敗メッセージに形式と N エディションの注記が入る
- **`OpenFailureMessage_Mp3_ContainsFormatWithoutHint`** — `.mp3` では注記が付かない
- **`OpenAudioFile_MissingM4a_ThrowsWithFormatInMessage`** — 開けなかったら `InvalidOperationException`（形式入り・内部例外付き）

> **テストで守れない範囲:** 実際の AAC デコード。scratchpad の `m4acheck` で実測した（下記）。

## 9. 未解決の質問

1. **VBR AAC の `TotalTime`** — 実ファイルが手に入ったら確かめる。*既定案: そのまま出す（進捗の分母がずれるだけで、文字起こし自体は末尾まで走る）。*

## 10. 前提

- Windows 10/11 の通常エディションには AAC デコーダー（Media Foundation）が入っている。

---

## 実測（2026-09-22、scratchpad `m4acheck`。本体の `AudioFileReader` / `TryGetAudioDuration` を呼ぶ）

| 入力 | M4A の `TotalTime` | 全デコード長 | 差 | 元 MP3 との差 |
|---|---|---|---|---|
| 6.07 秒 MP3 → AAC 128kbps | 00:00:06.080 | 00:00:06.080 | **0 ms** | +8 ms |
| 15 分 47 秒 MP3 → AAC 128kbps | 00:15:47.374 | 00:15:47.374 | **0 ms** | −9 ms |

- `.m4a` は `AudioFileReader` → `MediaFoundationReader` で開けた（`WaveFormat` は 32bit float）
- `TryGetAudioDuration`（開始時刻推定③の経路）も `true` を返した
- 副産物: **NAudio 2.2.1 の `AudioFileReader` は Windows では `.mp3` も `MediaFoundationReader` で開く**
  （スタックトレースで確認）。REQ-TRX-FILE-15 の注記「MP3 のフレーム表を作るためにファイル全体を走査」は
  `Mp3FileReader` の挙動であり、現行の経路では当たらない可能性がある（未計測。挙動には影響しないので本タスクでは触れない）

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 255 件成功 / 0 件失敗 / 0 件スキップ（244 → 255、+11）
- 計画からの逸脱: `OpenFailureMessage` を `OpenAudioFile` から切り出した（CA1308 で `ToLowerInvariant` が使えず、拡張子は入力どおりの大文字小文字で表示する）
