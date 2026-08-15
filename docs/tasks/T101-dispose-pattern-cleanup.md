# T101 — Dispose パターンの是正

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ハーネス導入時に一時緩和した破棄まわりの静的解析警告（CA1063 / CA1816 / CA1001 / CA2213 / CA2000）を
実際に解消し、`.editorconfig` の負債行を削除する。

## 2. スコープ境界

**やること**
- `AudioCaptureService` / `TranscriptionService` / `MainViewModel` に `Dispose(bool)` パターンを実装する（CA1063 / CA1816）
- `MainWindow` を `IDisposable` にする（CA1001 — 破棄可能な `_viewModel` を所有している）
- `MainViewModel._fileTranscriptionCts` を `Dispose` で破棄する（CA2213）
- `TranscriptionService.TranscribeFileAsync` の `StreamWriter` / `AudioFileReader` / `WhisperProcessor` を
  `using` に載せる（CA2000）
- `.editorconfig` から T101 の負債 5 行を削除する

**やらないこと（重要）**
- **`catch (Exception)` の是正（CA1031）** — T103 の担当。今回は触らない
- **同期呼び出しの非同期化（CA1849）／`CancellationToken` 伝播（CA2016）** — T104 の担当。
  今回 `using` に載せた 3 つは同期 `Dispose` のままにする（`await using` 化は T104）
- **`static` 化（CA1822）／空配列（CA1825）** — T105 の担当
- **録音・文字起こしの振る舞い**（破棄の順序・タイミングは現状を保つ）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | CA1063 を `sealed` で満たすか `Dispose(bool)` で満たすか | **`Dispose(bool)` パターン**。[30-coding-standards.md §7](../harness/30-coding-standards.md#7-リソース破棄) が明示的にこちらを規定しているため。`sealed` 化は WPF の `MainWindow` / テストからの利用に余計な制約を持ち込む |
| D2 | `MainWindow`（CA1001）の扱い | **`IDisposable` を実装し、`Closed` から `Dispose()` を呼ぶ。** 抑止（`#pragma`）では逃げない。既存の `Closed += _viewModel.Dispose()` と等価な挙動 |
| D3 | CA2000（`TranscribeFileAsync`）の直し方 | **本体を `TranscribeFileCoreAsync` に切り出し、`using` 宣言で破棄する。** 例外は Core を抜ける時点で `using` が破棄を終えてから外側の `catch` に届くため、`TryDeleteFile` はファイルハンドル解放後に走る（現状と同じ順序） |
| D4 | ファイナライザーを足すか | **足さない。** 4 型ともアンマネージドリソースを直接持たない（NAudio / Whisper.net のハンドルは各ライブラリ側が保持）。`Dispose(bool)` の `disposing == false` 側は空 |

## 4. 仕様書への影響

- [x] `docs/spec/03_class_diagram.md` — `MainWindow` に `<<IDisposable>>` と `+Dispose()` を追加する
- 他章は影響なし。理由: 振る舞い・層構成・処理順序を変えない静的解析の是正であるため
  （[50-spec-standards.md §2](../harness/50-spec-standards.md#2-章立てと更新対象の判定)「更新が不要な変更」）

## 5. アーキテクチャへの影響

- ADR: **不要**。層構成・依存方向・スレッドモデルのいずれも変わらない。
  `Dispose(bool)` の追加は既存クラス内部の実装であり、[ADR-0001](../adr/0001-baseline-architecture.md) の
  「DI・インターフェース抽象を使わない」方針にも触れない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AudioCaptureService.cs` | `Dispose()` → `Dispose()` + `protected virtual Dispose(bool)` + `GC.SuppressFinalize(this)` |
| `AudioCaptureApp/Services/TranscriptionService.cs` | 同上。加えて `TranscribeFileAsync` を分割し `using` 化（CA2000） |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 同上。加えて `_fileTranscriptionCts` を破棄（CA2213） |
| `AudioCaptureApp/MainWindow.xaml.cs` | `IDisposable` 実装、`Closed` から `Dispose()` を呼ぶ（CA1001） |
| `.editorconfig` | T101 の負債 5 行（CA1063 / CA1816 / CA1001 / CA2213 / CA2000）を削除 |
| `docs/spec/03_class_diagram.md` | `MainWindow` の記述を更新 |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** `docs/spec/03_class_diagram.md` の `MainWindow` を更新

### グループ B — Dispose(bool) パターン
- [x] **B1** `AudioCaptureService` に `Dispose(bool)` を実装
- [x] **B2** `TranscriptionService` に `Dispose(bool)` を実装
- [x] **B3** `MainViewModel` に `Dispose(bool)` を実装し、`_fileTranscriptionCts` を破棄
- [x] **B4** `MainWindow` を `IDisposable` にする

### グループ C — CA2000
- [x] **C1** `TranscribeFileAsync` を `TranscribeFileCoreAsync` に分割し `using` 宣言へ

### グループ D — 緩和の削除
- [x] **D1** `.editorconfig` の T101 負債 5 行を削除
- [x] **D2** [40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和) の負債表から T101 行を削除

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

**追加なし。** 理由: 本タスクは破棄経路の是正であり、対象 4 型はいずれも実オーディオデバイス／
Whisper モデル実体を要求するため、ユニットテストで `Dispose` を通せない
（[40-quality-gates.md §4](../harness/40-quality-gates.md#4-既知の穴)「実機依存の挙動」）。
既存の 26 テストが回帰していないことで担保する。

> **テストで守れない範囲:** `Dispose` の多重呼び出し安全性、`TranscribeFileCoreAsync` の
> 例外時破棄順序。前者はコードレビュー、後者は D3 の根拠（`using` のスコープ規則）で担保する。

## 9. 未解決の質問

なし。

## 10. 前提

- 4 型はいずれもアンマネージドリソースを直接保持しない（D4 の根拠）。
- `WhisperProcessor` は `IDisposable`（既存コードが `processor?.Dispose()` を呼んでいる事実による）。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: なし
