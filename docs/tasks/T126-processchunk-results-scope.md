# T126 — ProcessChunk がキャンセル時に完了済み区間の行を捨てる

> **状態:** 完了 — 2026-08-20
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ライブ文字起こしで、Whisper が例外（キャンセル含む）を投げたときに **その時点までに確定していた行が
`.txt` に書かれない**。それらの行は `SegmentTranscribed` イベントで画面には既に出ているため、
画面と `.txt` が食い違う。書き出しを例外経路からも必ず通るようにする。

## 2. スコープ境界

**やること**
- `TranscriptionService.ProcessChunk` の `results` を `try` の外へ出し、例外・キャンセルの
  いずれで抜けても、確定済みの行を `.txt` へ追記する。
- 追記処理そのものを `internal static` の純粋寄りヘルパー（`AppendTranscriptLines`）へ切り出し、
  ファイル I/O の失敗が `TranscriptionLoop` へ漏れないようにする。

**やらないこと（重要）**
- **`ProcessFileChunkAsync`（ファイル文字起こし）側は変えない。** あちらは `StreamWriter` へ
  逐次 `WriteLineAsync` しており「途中まで書いた行が残る」構造ではない。加えて
  REQ-TRX-FILE-07 で **キャンセル時は出力ファイルごと削除する** と決めており、
  「部分結果を残す」という本タスクの方針と正反対である。混ぜてはいけない。
- **`SegmentTranscribed` の発火タイミングは変えない。** 画面表示が先行すること自体は仕様。
- **区間ループの打ち切り条件（T127 の `interruptible`）は変えない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 書き出しを `finally` に置くか、`catch` の後ろに置くか | **`catch` の後ろ（try/catch を抜けた直後）に置く。** `finally` だと、そこで発生した `IOException` が catch 節を通らずに `TranscriptionLoop` まで抜けてワーカースレッドを落とす |
| D2 | 追記の失敗をどう扱うか | **`Error` イベントに変換して継続する。** 書き出せなかった行は失われるが、ワーカーを落として以降のチャンクを全滅させるより良い |
| D3 | 部分結果を残すか、捨てるか | **残す。** 画面に出た行と `.txt` を一致させることが本タスクの目的。ライブ文字起こしの `.txt` は元々「録音中に追記され続ける」ものであり、途中で終わった状態が正常な形である（ファイル文字起こしの `.transcript.txt` とは性質が違う。§2 参照） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-07 に「ライブ側は区間の途中で中断しても、
      確定済みの行を必ず追記する」を追記。ファイル側との差（REQ-TRX-FILE-07）も明記
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService.AppendTranscriptLines` を追加
- [x] `docs/spec/04_sequence_diagram.md` — §3 の書き出しステップを「例外・キャンセル時も通る」よう修正
- [ ] `docs/spec/02_architecture.md` — 層・スレッドモデルは不変のため変更なし

## 5. アーキテクチャへの影響

なし。Service 層内部の制御フローの修正であり、層構成・依存方向・スレッドモデルに触れない。

- ADR: 不要

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `ProcessChunk` の `results` を try の外へ。`AppendTranscriptLines` を追加し、追記失敗を `Error` に変換 |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `AppendTranscriptLines` のテストを追加 |

## 7. 実装手順

### グループ A — 書き出し経路の是正
- [x] **A1** `AppendTranscriptLines(string, IReadOnlyList<string>)` を `internal static` で追加し、
      空リストなら何もせず、`IOException` / `UnauthorizedAccessException` を呼び出し元へ返す形にする
- [x] **A2** `ProcessChunk` の `results` 宣言を `try` の外へ移し、try/catch を抜けた直後に追記する

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`AppendTranscriptLines_EmptyList_DoesNotCreateFile`** — 有声区間が 0 件のときにファイルを作らない
- **`AppendTranscriptLines_AppendsToExistingFile`** — 既存ファイルの末尾へ追記し、既存行を消さない
- **`AppendTranscriptLines_MissingDirectory_ReturnsError`** — 書き出し先が無い場合に例外を投げず失敗を返す

> **テストで守れない範囲:** 「Whisper がキャンセル例外を投げたときに確定済みの行が残る」こと自体は、
> Whisper のネイティブ処理を必要とするためユニットテストで再現できない
> （ハーネス §7 の「Whisper モデルの実体を要求するテストは書かない」）。
> ここで固定できるのは追記ヘルパーの振る舞いまでで、`results` のスコープはコードレビューで守る。

## 9. 未解決の質問

なし（D1〜D3 で確定）。

## 10. 前提

- ライブ文字起こしの `.txt` は「録音中に追記され続けるログ」であり、途中で終わった状態でも
  ユーザーにとって有用である。
- `File.AppendAllLines` は行単位のアトミック性を保証しないが、単一のワーカースレッドからしか
  呼ばれないため、行が混ざることはない。

---

## 実行結果 (2026-08-20)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 99 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: なし

## 11. 積み残し・気づいたこと

- **`SegmentTranscribed` と `.txt` の完全一致は、まだ保証されていない。**
  本タスクで「Whisper の例外で行が消える」経路は塞いだが、`AppendTranscriptLines` 自体が
  失敗した場合（ディスクフル・排他ロック等）は画面に出た行が `.txt` に残らない。
  そのときは `Error` イベントでユーザーに伝わるが、行の再送は行わない。
  現実の発生頻度が低いと判断して受け入れた。再送が必要になったら別タスクで起票する。
