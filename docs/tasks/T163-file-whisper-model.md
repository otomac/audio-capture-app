# T163 — ライブ文字起こしとファイル文字起こしで Whisper モデルを別々に指定できるようにする

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **依存:** [T162](./T162-whisper-model-registry.md)（登録一覧）

## 1. 目的

ライブは軽いモデル（small）で追従性を、ファイルは重いモデル（large 系）で精度を、と使い分けられるようにする。

## 2. スコープ境界

**やること**
- オプション指定ダイアログにモデルのドロップダウン（REQ-TRX-FILE-17）
- `AppSettings.FileWhisperModelName`（REQ-CFG-09）
- `TranscriptionService.TranscribeFileAsync` が `FileTranscriptionOptions.ModelPath` を受け、ライブ用と違えば 2 つ目の `WhisperFactory` を実行の間だけ作る
- 読み込み失敗をダイアログ内に出し、閉じない
- `CanTranscribeFromFile` を「登録済みモデルが 1 つ以上」に変える（REQ-TRX-FILE-01）
- large 系をライブ用と同時に GPU へ載せられることを実測する

**やらないこと（重要）**
- **ライブ側の読み込みの流れ（`TryLoadWhisperModel`）は変えない**
- **ファイル用モデルを常駐させない**（実行の間だけ。処理後に破棄）
- **GPU 設定をファイル側で別に持たない**（ライブ側の設定に従う）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ファイル用モデルの載せ方 | **(a) 実行のたびに 2 つ目の `WhisperFactory` を作って処理後に破棄する**（既定案）。ライブ用と同じ実パスが選ばれ、かつライブ用が読み込み済みなら `_factory` を共有して読み込み直さない |
| D2 | `CanTranscribeFromFile` | **「登録済みモデルが 1 つ以上ある」**（既定案）。実際の読み込みは「開始」で行う |
| D3 | 永続化 | **`AppSettings.FileWhisperModelName`**（既定案）。名前で持つ。モデル管理で名前を変えたときは追随、削除・未登録ならライブ用へ倒す |
| D4 | 読み込み失敗の見せ方 | `TranscribeFileAsync` の戻り値を `FileTranscriptionResult(Outcome, Message)` にし、`ModelLoadFailed` のときは VM が `FileTranscriptionModelError` に理由を入れ、`StartFileTranscriptionAsync` が `false` を返してダイアログを閉じない（REQ-TRX-FILE-12 の自動クローズは処理を始めた後だけ） |
| D5 | GPU | `FileTranscriptionOptions.UseGpu` にライブ側の `UseGpuForTranscription` を渡す（GPU が使えないと判明していれば既に false になっている。REQ-GPU-02） |
| D6 | ライブ用が未読み込み（失敗・未設定）のとき | 同じパスでも 2 つ目として読み込む（共有できる相手がいないため） |
| D7 | 読み込み中の表示 | 進捗に `モデル読み込み中` フェーズ（`Total` = 0）を報告する。中止は読み込み後の境界で効く |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-CFG-01、**REQ-CFG-09 新設**、REQ-TRX-04、REQ-TRX-FILE-01 / 09、**REQ-TRX-FILE-17 新設**
- [x] `docs/spec/03_class_diagram.md` — `FileTranscriptionOptions.ModelPath / UseGpu`、`FileTranscriptionResult`、`TranscribeFileAsync` の戻り値、`MainViewModel` の追加メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §6 に 2 つ目の factory の分岐
- 変更なし: `02_architecture.md`

## 5. アーキテクチャへの影響

- ADR: **不要**。`TranscriptionService` の内部で factory を 1 つ増やすだけ。層・依存方向・ライブラリに変更なし。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Models/AppSettings.cs` | `FileWhisperModelName` |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `FileTranscriptionOptions.ModelPath / UseGpu`、`FileTranscriptionResult`、`LoadedModelPath`、factory の選択と破棄 |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `SelectedFileWhisperModel` / `FileTranscriptionModelError`、`CanTranscribeFromFile`、結果の扱い |
| `AudioCaptureApp/ViewModels/MainViewModel.WhisperModels.cs` | 名前変更・削除の追随、`TranscribeFromFileCommand` の再評価 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `SaveSettings` |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml(.cs)` | ドロップダウン、失敗表示、閉じない分岐 |
| `AudioCaptureApp.Tests/*` | 既定選択の純粋関数、設定の往復 |

## 7. 実装手順

- [x] **A1** `FileTranscriptionOptions` に `ModelPath` / `UseGpu`、`FileTranscriptionResult`
- [x] **A2** `TranscribeFileAsync`: factory の選択（共有 or 2 つ目）と破棄
- [x] **A3** VM: 選択・既定・失敗表示・`CanTranscribeFromFile`
- [x] **A4** XAML / code-behind
- [x] **A5** 実測（scratchpad harness）
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`FileWhisperModelFor_SavedNameExists_SelectsIt`** — 保存名が一覧にあればそれ
- **`FileWhisperModelFor_SavedNameMissing_FallsBackToLive`** — 無ければライブ用
- **`FileWhisperModelFor_NoLive_FallsBackToFirst`** — ライブ用も無ければ先頭、一覧が空なら `null`
- **`ShouldShareLiveFactory_SamePathAndLoaded_IsTrue`** / **`_DifferentPathOrNotLoaded_IsFalse`** — 共有の判定
- **`JsonRoundTrip_PreservesFileWhisperModelName`**

> **テストで守れない範囲:** 2 つ目の factory の生成・破棄（Whisper 実体が要る）。scratchpad で実測する。

## 9. 未解決の質問

なし。

## 10. 前提

- `WhisperFactory` は同一プロセスで複数同時に持てる（Whisper.net はネイティブランタイムをプロセスで 1 度だけ読み込み、factory はモデルごとのコンテキスト）。実測で確認する。

---

## 実測（2026-09-22、scratchpad `factorycheck`。本体の `TranscriptionService` を直接呼ぶ。RTX 3050 6GB / Vulkan）

| # | 操作 | 結果 | GPU 使用メモリ（nvidia-smi） |
|---|---|---|---|
| 0 | 起動直後 | — | 959 MiB |
| 1 | ライブ用 `ggml-small`（487MB）を `LoadModel` | 1.2 秒 | 1,436 MiB（+477） |
| 2 | 同じモデルを指定して 15 分 47 秒の会議を文字起こし | **共有**（読み込み無し）。311 行 / 211.7 秒 | 1,437 MiB（変化なし） |
| 3 | `ggml-large-v3-turbo`（1.6GB）を指定して同じ音声を文字起こし | **2 つ目の factory**。346 行 / 344.0 秒 | 処理中 **2,995 MiB**（+1,559）→ 処理後 **1,437 MiB**（戻った） |
| 4 | 存在しないパス | `ModelLoadFailed` 「モデルファイルが見つかりません: …」 | — |
| 5 | 1KB のダミーファイル | `ModelLoadFailed` 「Whisperモデル読み込み失敗 (broken.bin): Failed to load the whisper model.」 | — |
| 6 | 終了時 | ライブ用は読み込まれたまま（`IsModelLoaded = true`） | 1,437 MiB |

- #3 の出力は、以前に単独で `ggml-large-v3-turbo` を使って作った同じ音声の `.transcript.txt` と **md5 が一致**した
  （`bd3616ac…`）。2 つ目の factory でも結果は変わらない。
- 6 秒の短い音声でも同じ流れを通した（共有 89 ms / 2 つ目 4.5 秒、処理中 1,959 MiB → 1,433 MiB）。
- **注意（作業ログ）:** 最初の実行では会議ファイルを `Documents\AudioCapture` から直接読んだため、出力先が同じフォルダの
  既存の `.transcript.txt` になった。サンドボックスの書き込み拒否で**既存ファイルは無傷**だった（サイズ・日時・md5 とも変化なし）が、
  以後は音声を scratchpad へコピーしてから実行した。

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 286 件成功 / 0 件失敗 / 0 件スキップ（280 → 286、+6）
- 計画からの逸脱: なし
