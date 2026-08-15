# T103 — `catch (Exception)` の局所化と理由付与

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

広すぎる例外捕捉（CA1031）13 箇所について、**絞れるものは型を絞り**、
**絞れないものは理由を明記して局所抑止**する。そのうえで `.editorconfig` の負債行を削除する。

## 2. スコープ境界

**やること**
- CA1031 の 13 箇所を 1 件ずつ判定し、「型を絞る」か「`#pragma` で局所抑止＋理由 1 行」に振り分ける
- `.editorconfig` から T103 の負債 1 行を削除する

**やらないこと（重要）**
- **例外処理の設計そのものの変更** — どこで捕まえるか・捕まえた後にどうするか（イベント通知・
  既定値へのフォールバック）は現状を保つ。本タスクは「捕捉の広さ」だけを扱う
- **CA1031 のプロジェクト全体緩和** — 台帳が本ルールを「恒久緩和」ではなく「負債」と位置づけている
  （[40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和)）ため、
  緩和へ格下げする判断は本タスクでは行わない
- **T104 / T105 の対象ルール**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 全 13 箇所の振り分け方針 | **ネイティブ／COM／ワーカースレッド境界は広い捕捉を維持し `#pragma` ＋理由。純粋なファイル I/O と JSON のみを触る箇所は型を絞る** |
| D2 | 抑止の書き方 | **`#pragma warning disable CA1031` / `restore` を該当 `catch` の直前・直後に置く。** 対象を最小範囲に絞れるため（[40-quality-gates.md §3-1](../harness/40-quality-gates.md#3-1-局所的な抑止推奨)） |
| D3 | 広い捕捉を維持する理由の共通根拠 | **プロセスを落とさないことが機能要件**。オーディオコールバック・ライタースレッド・文字起こしスレッドで例外を漏らすとアプリごと落ち、録音中のデータを失う |

### 振り分け表（13 箇所）

| # | 箇所 | 判定 | 理由 |
|---|---|---|---|
| 1 | `AudioCaptureService.TryApplyMuteToEndpoint` | 抑止 | `AudioEndpointVolume` は COM。権限不足等で任意の例外。失敗してもソフトミュートで継続する |
| 2 | `AudioCaptureService.EnumerateDevices` | 抑止 | 既定デバイス無しは正常系。COM 由来の例外型を列挙し切れない |
| 3 | `AudioCaptureService.StopMicMonitor`（購読解除） | 抑止 | 停止処理は失敗しても後続の破棄を続ける必要がある |
| 4 | `AudioCaptureService.StopMicMonitor`（`StopRecording`） | 抑止 | 同上 |
| 5 | `AudioCaptureService.WriterLoop` | 抑止 | ワーカースレッド境界。漏らすとプロセスが落ちる。`RecordingError` に変換する |
| 6 | `AudioCaptureService.CleanupLoopback` | 抑止 | 停止処理は失敗しても後続の破棄を続ける必要がある |
| 7 | `SettingsService.Load` | **絞る** | `File.ReadAllText` と `JsonSerializer` のみ。想定される型を列挙できる |
| 8 | `TranscriptionService.LoadModel` | 抑止 | Whisper ネイティブライブラリのロード。`DllNotFoundException` 等、型を列挙し切れない |
| 9 | `TranscriptionService.TryDeleteFile` | **絞る** | `File.Delete` のみ。想定される型を列挙できる |
| 10 | `TranscriptionService.ProcessChunk` | 抑止 | ワーカースレッド境界＋ネイティブ処理。`Error` イベントに変換する |
| 11 | `TranscriptionService.TranscribeFileAsync` | 抑止 | ネイティブ処理。キャンセル判定のため全例外を見る必要がある |
| 12 | `MainViewModel.TryLoadWhisperModel` | 抑止 | `async void`。漏らすとプロセスが落ちる。画面のステータスに変換する |
| 13 | `MainViewModel.RunFileTranscriptionAsync` | 抑止 | UI コマンド境界。任意の失敗を画面のステータスに変換する |

> #11 は T101 の分割後も残る `catch (Exception ex)`（キャンセル判定）である。

## 4. 仕様書への影響

- [x] `docs/spec/02_architecture.md` — **更新なし（確認のみ）**。§7「エラーハンドリング方針」は
      すでに「イベントで非同期に通知」「デバイス固有の機能制限は catch して機能を縮退させ処理継続を
      優先する」と書かれており、本タスクの振り分け（D3・振り分け表）と一致している

理由: 例外の捕捉範囲と復帰動作は現状を保つため、外から見た振る舞いは変わらない。

## 5. アーキテクチャへの影響

- ADR: **不要**。層・依存方向・スレッドモデルのいずれにも触れない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AudioCaptureService.cs` | 6 箇所に `#pragma` ＋理由 |
| `AudioCaptureApp/Services/SettingsService.cs` | `Load` の捕捉型を絞る |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TryDeleteFile` を絞る、3 箇所に `#pragma` ＋理由 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 2 箇所に `#pragma` ＋理由 |
| `.editorconfig` | T103 の負債 1 行（CA1031）を削除 |

## 7. 実装手順

### グループ A — 型を絞る
- [x] **A1** `SettingsService.Load` の捕捉型を絞る
- [x] **A2** `TranscriptionService.TryDeleteFile` の捕捉型を絞る

### グループ B — 局所抑止＋理由
- [x] **B1** `AudioCaptureService` の 6 箇所
- [x] **B2** `TranscriptionService` の 3 箇所
- [x] **B3** `MainViewModel` の 2 箇所

### グループ C — 緩和の削除
- [x] **C1** `.editorconfig` の T103 負債 1 行を削除
- [x] **C2** [40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和) の負債表から T103 行を削除

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** `docs/spec/02_architecture.md` のエラー方針と食い違いが無いか確認

## 8. テスト一覧

**追加なし。** 絞った 2 箇所（#7 / #9）はいずれもファイルシステム状態に依存し、
既存テストはファイル I/O を伴わない純粋関数のみを対象にしている
（[40-quality-gates.md §4](../harness/40-quality-gates.md#4-既知の穴)）。

> **テストで守れない範囲:** 絞った 2 箇所で**想定外の型**が飛んだ場合の挙動。
> #7 は起動時のフォールバック（既定設定）、#9 は削除失敗の黙殺であり、
> どちらも漏れると呼び出し元へ伝播する。振り分け表の根拠（触れる API が限定的であること）で担保する。

## 9. 未解決の質問

なし。振り分けは §3 の表で確定済み。

## 10. 前提

- CA1031 の指摘箇所は、緩和を外したビルドで実測した 13 件がすべてである。
- 抑止 11 箇所は「誤検知」ではなく「意図的な設計」である。CA1031 は設計意図を判別できないため
  局所抑止が正しい対処になる。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: なし
- 補足: `.editorconfig` の `dotnet_remove_unnecessary_suppression_exclusions = none` により
  IDE0079（不要な抑止）が有効なため、ビルド 0 警告は **11 箇所の `#pragma` がいずれも
  実際に必要である**ことも同時に示している
