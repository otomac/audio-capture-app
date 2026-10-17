# T183 — 遅れているときの早期確定抑止が発火しない／ギャップ分割のチャンクが 20 秒を超える

> **状態:** 完了 — 2026-10-03
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

文字起こしが遅れているとき（Zoom 会議中に GPU を取られたときなど）に、遅れをさらに広げる 2 つの不具合を直す。

- ① 遅れているときに末尾無音での早期確定を止める仕組み（REQ-TRX-LIVE-13、T175 の改善 A1）が、一度も発火しない
- ② ギャップ分割（REQ-TRX-LIVE-07）で確定するチャンクが 20 秒の上限（REQ-TRX-LIVE-10）を超え、1 回の Whisper 呼び出しが数分分になる

## 2. スコープ境界

**やること**
- ① 遅れの判定に使う滞留を「全ソースの確定済みチャンクと未確定バッファの合計」にする
- ② ギャップ分割で確定するバッファを、先頭から 20 秒分ずつのチャンクに分ける

**やらないこと（重要）**
- **閾値（`BacklogSuppressEndpointingSamples` = 60 秒分、`BufferThresholdSamples` = 20 秒分、`GapThreshold` = 500ms）は変えない**
- **`ChunkTakeCount` の判定順と中身は変えない**（渡す `suppressEndpointing` の求め方だけを直す）
- **遅れ警告（REQ-REC-07、`LagWarningSeconds`）と停止時の待ち方（REQ-TRX-LIVE-11）は変えない**
- **ソース間の処理順（ソースごとに取り出せるだけ処理する）は変えない**

## 3. 原因

**①** `TakeNextChunk` は確定済みチャンク（`Ready`）があれば先に返す。滞留を数える箇所に来るのは `Ready` が空のときだけなので、
`Ready` の合計で数えていた滞留は常に 0 だった。未確定バッファ（`Pcm16kBuffer`）を足しても足りない — 20 秒分以上あれば
`ChunkTakeCount` が先に 20 秒で切り出すため、末尾無音の判定に来るときの自ソース分は常に 20 秒未満で、60 秒の閾値に届かない。
遅れは「一方のソースを処理している間に、もう一方のソースのバッファへ溜まる」形で現れるので、全ソースの合計で見る必要がある。
既存のテストは `ChunkTakeCount` に `suppressEndpointing: true` を直接渡していたため、この経路を通らず気づけなかった。

**②** `AddSamples` はギャップ（500ms 超の供給の途切れ）を見つけると、それまでのバッファ全体を 1 チャンクとして `Ready` に積む。
遅れている間はワーカーがバッファを取りに来ないので、バッファは数分分に膨らみうる。その状態でミュートなどのギャップが入ると、
数分分のチャンクがそのまま Whisper に渡る。

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 滞留の数え方 | **全ソースの `Ready` と `Pcm16kBuffer` の合計**（`PendingSeconds` から処理中のチャンクを除いた量）。チャンクを取り出す時点では処理中のチャンクは無いので、`PendingSeconds` と同じ量を見ていることになる。遅れ警告（`LagWarningSeconds` = 60 秒、`PendingSeconds` で判定）とも揃う |
| D2 | 数える場所 | **ワーカーが `TakeNextChunk` を呼ぶ前に、ソースの錠の外で数えて引数で渡す**。`TakeNextChunk` の中（ソースの錠を持った状態）で `_sourcesLock` を取ると、`PendingSeconds`（`_sourcesLock` → 各ソースの錠）と錠の順序が逆になりデッドロックしうる |
| D3 | 集計の共通化 | `PendingSeconds` の集計を `PendingBufferedSamples()`（全ソース）と `BufferedSamples(state)`（1 ソース）に切り出し、ワーカーと共用する |
| D4 | ギャップ分割の上限 | **バッファを先頭から 20 秒分ずつに分けて `Ready` に積む**（`SplitAtChunkLimit`）。端数は最後のチャンクになる。各チャンクの先頭時刻は `RegionStart(バッファ先頭, 位置)`（バッファ内は地続きなので REQ-TRX-LIVE-08 と矛盾しない） |
| D5 | テストのための可視性 | `PendingChunk` / `SourceState` / `TakeNextChunk` を `private` → `internal` にする（既存の `ChunkTakeCount` などと同じ扱い）。`SourceState` は `sealed` にする |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-07（20 秒分を超えるバッファは 20 秒ずつに分けて確定）、REQ-TRX-LIVE-10（ギャップ分割にも適用）、
  REQ-TRX-LIVE-13（滞留は全ソースの合計で数える。自ソース分だけでは足りない理由）
- 変更なし: `02_architecture.md`（スレッドモデル・データフローは同じ）、`03_class_diagram.md`（追加・変更したのは `internal` / `private` のメンバーで、図に載せていない）、
  `04_sequence_diagram.md`（処理順は同じ）

## 6. アーキテクチャへの影響

- ADR: **不要**。層構成・依存方向・スレッドモデルに触れない。ワーカーが `_sourcesLock` を取るようになるが、錠の順序は `PendingSeconds` と同じ（`_sourcesLock` → 各ソースの錠）

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `SplitAtChunkLimit` を追加し `AddSamples` のギャップ分割で使う。`TakeNextChunk` に `pendingSamples` を足し、自ソースの `Ready` での集計を削除。`PendingSeconds` の集計を `PendingBufferedSamples` / `BufferedSamples` に切り出す。可視性の変更（D5） |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 下のテストを追加 |

## 8. 実装手順

### グループ A — 遅れの判定（①）
- [x] **A1** `PendingSeconds` の集計を `PendingBufferedSamples` / `BufferedSamples` に切り出す
- [x] **A2** `TakeNextChunk` に `pendingSamples` を足し、ワーカーの 2 か所（通常運転・排出）から `PendingBufferedSamples()` を渡す

### グループ B — ギャップ分割の上限（②）
- [x] **B1** `SplitAtChunkLimit` を追加し、`AddSamples` のギャップ分割で使う

### グループ C — テスト
- [x] **C1** 下の 7 件を追加する

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件（CI・Release 構成。「実行結果」を参照）
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし（同上）
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 392 件成功 / 0 件失敗（同上）
- [x] **Z4** 仕様書（§5）の更新反映を読み直す

## 9. テスト一覧

- **`TakeNextChunk_NotBehindAndSilentTail_TakesWholeBuffer`** — 遅れていなければ、末尾無音で早期確定する（従来の挙動が変わらない）
- **`TakeNextChunk_OtherSourceBehind_WaitsForFullChunk`** — 自ソースは 7 秒分しか無くても、他ソースの滞留と合わせて 60 秒分以上なら早期確定しない（①の回帰防止）
- **`BufferedSamples_CountsReadyChunksAndBuffer`** — 1 ソースの滞留は確定済みチャンクと未確定バッファの合計
- **`SplitAtChunkLimit_UnderLimit_ReturnsWholeBufferAsOneChunk`** — 20 秒分以下なら従来どおり 1 チャンク・先頭時刻はそのまま
- **`SplitAtChunkLimit_OverLimit_SplitsInto20SecondChunks`** — 50 秒分 → 20 / 20 / 10 秒、先頭時刻は 0 / +20 / +40 秒（②の回帰防止）
- **`SplitAtChunkLimit_ExactMultiple_HasNoEmptyChunk`** — ちょうど 40 秒分なら 2 チャンクで、空のチャンクを作らない
- **`SplitAtChunkLimit_KeepsSampleOrder`** — 分けたチャンクをつなぐと元のバッファに戻る

> **テストで守れない範囲:** ワーカーが `PendingBufferedSamples()` を渡していること、`AddSamples` が `SplitAtChunkLimit` を使っていることは、
> セッションの開始に Whisper のモデルが要るためユニットテストでは通せない。コードで読む。
> 実機で遅れを再現したときに（Zoom 会議中など）、遅れが縮むかどうかは利用者の手元で確かめる。

## 10. 未解決の質問

なし。

## 11. 前提

- ワーカーは `_sources` の要素を増減しない（T165 D6）。ワーカーが `_sourcesLock` を取っても、取るのは `TakeNextChunk` の呼び出し前（どのソースの錠も持っていない）
- 2 ソースの合計で 60 秒分を判定するのは、Whisper の処理時間が音源によらず音声の長さに比例するため（遅れの大きさは合計で決まる）

## 実行結果 (2026-10-03)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 392 件成功 / 0 件失敗 / 0 件スキップ（変更前 385 件 + 追加 7 件）
- 実行場所: このクラウド環境には dotnet が無いため、ブランチ上で CI（`build-desktop`、windows-latest、workflow_dispatch）を動かして測った
  （run 37103710182、commit `bfcae3d`）。**CI は Release 構成**で、ゲートの規定（Debug）とは構成が違う。
- 実機での確認（遅れている状態で早期確定が止まり、遅れが縮むか。遅れている間のミュートで 20 秒を超えるチャンクができないか）は未実施。利用者の手元で行う
