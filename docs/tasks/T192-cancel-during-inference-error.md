# T192 — 打ち切りで推論中にキャンセルすると「文字起こしエラー」が出る

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **規範:** [20-architecture-standards.md](../harness/20-architecture-standards.md) §3 規則 7

## 1. 目的

停止の「打ち切り」（`RequestAbort`）や `Dispose` で確定パスの推論中にキャンセルしたとき、Whisper.net が投げる
`WhisperProcessingException` を「文字起こしエラー: …」として利用者に見せないようにする。打ち切りは利用者が選んだ操作であり、エラーではない。

## 2. 再現（2026-10-12、修正前）

スクラッチの検証プログラムで、確定用 medium（GPU）・速報 base（CPU）・2 音源を実時間で流し、滞留 44〜52 秒の状態で `RequestAbort` → `StopSession` した。

| 回 | 打ち切った時刻 | 出たエラー |
|---|---|---|
| 1 | 27 秒 | 文字起こしエラー: Failed to decode during token processing. |
| 2 | 34 秒 | 文字起こしエラー: Failed to decode audio features. |
| 3 | 41 秒 | 文字起こしエラー: Failed to decode audio features. |

3 回とも出た。メッセージは T182 で見た「Failed to encode audio features」だけでなく、推論のどの段階でキャンセルされたかで変わる。

## 3. スコープ境界

**やること**
- `ProcessChunk` で、キャンセル済みのときの例外を `Error` に変換しない（`OperationCanceledException` と同じく正常な中断として扱う）

**やらないこと（重要）**
- **キャンセルしていないときの例外の扱いは変えない**（従来どおり「文字起こしエラー: …」）
- **打ち切った区間を `.txt` に残さない挙動は変えない**（確定済みの行の追記・確定完了の通知は従来どおり行う）

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 判定 | 例外の型ではなく**トークンがキャンセル済みか**で決める。Whisper.net の例外メッセージは段階で変わり（§2）、型も `OperationCanceledException` とは限らないため。ファイル文字起こし（`TranscribeFileAsync`）と速報（T186）も同じ判定をしている |
| D2 | テストの形 | 判定を `internal static bool ShouldReportChunkError(Exception, bool canceled)` に切り出してテストする |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-11 の「キャンセル後の例外はエラーとして通知しない」を、速報だけでなく確定パスにも掛かる書き方にする

## 6. アーキテクチャへの影響

- ADR: 不要（ハーネス §3 規則 7 に合わせる修正）。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `ProcessChunk` の catch と `ShouldReportChunkError` |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 判定のテスト |

## 8. テスト一覧

- **`ShouldReportChunkError_AfterCancel_IsSilent`** — キャンセル済みなら `WhisperProcessingException` でも通知しない
- **`ShouldReportChunkError_WithoutCancel_IsReported`** — キャンセルしていなければ通知する
- **`ShouldReportChunkError_OperationCanceled_IsSilent`** — `OperationCanceledException` は従来どおり通知しない

> **テストで守れない範囲:** 実際に推論中にキャンセルされること（モデルが要る）。§2 と同じ検証プログラムで、修正後にエラーが出ないことを確かめる。

## 9. 未解決の質問

なし。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 497 件成功 / 0 件失敗 / 0 件スキップ（T188 完了時 492 件 + 本タスク 5 件）
- **修正後の再現手順（§2 と同じ条件で 3 回）:** 打ち切りで出たエラー 0 件（修正前は 3 回とも 1 件）。停止は 0.3〜0.4 秒で完了し、打ち切る前に確定していた行（3 / 7 / 4 行）は `.txt` に残った
- 計画からの逸脱: なし
