# T190 — 確定パスのチャンク上限を、遅れていないときは 10 秒にする

> **状態:** 進行中 — 2026-10-03
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

利用者の依頼 (2026-10-03): 話し続けていると確定が 20 秒ごとにしか出ず、リアルタイム性が低い。確定を 10 秒程度に抑えたい。

末尾無音の契機（REQ-TRX-LIVE-13。2 秒の無音で確定）は、話し続けている間は来ない。会議では 2 秒止まらずに話し続けることが多く、
その間の確定はチャンクの上限（REQ-TRX-LIVE-10）ごとにしか出ない。上限を 20 秒から 10 秒にする。

## 2. スコープ境界

**やること**
- ライブ文字起こしのチャンク上限を、遅れていないときは 10 秒分、遅れているときは 20 秒分にする

**やらないこと（重要）**
- **速報パス（T181 / ADR-0007）の設計は変えない。** 利用者の選択 (2026-10-03): 速報は更新間隔 1 秒・遅れ p95 ≤ 2 秒のまま
  （T181 D9 の「8 秒」は句の長さの上限で、速報が出るまでの遅れではない。話し続けていても速報は約 1 秒ごとに更新する設計）
- **ファイル文字起こしの 20 秒チャンクは変えない**（`BufferThresholdSamples` はファイル側でも使っている）
- **ギャップ分割（REQ-TRX-LIVE-07）の上限は 20 秒のまま**（T183 の `SplitAtChunkLimit`）。遅れていないときはワーカーが毎ポーリングで
  10 秒分を取りに来るのでバッファは 10 秒を大きく超えず、20 秒を超えうるのは遅れているときだけだから
- **遅れの判定（全ソースの滞留 60 秒分以上）と末尾無音・供給途絶の契機は変えない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 上限 | **遅れていないとき 10 秒分（`LiveChunkSamples`）、遅れているとき 20 秒分（`BufferThresholdSamples`）。** 利用者の選択 (2026-10-03)。1 回の呼び出しが短いほど音声 1 秒あたりの処理が割高になる（実測: 20 秒入力 0.181 秒 / 5 秒入力 0.381 秒。10 秒は未実測）ため、遅れているときは処理能力を優先する |
| D2 | 「遅れている」の判定 | REQ-TRX-LIVE-13 と同じ（T183 の全ソースの滞留が `BacklogSuppressEndpointingSamples` = 60 秒分以上）。判定を 2 つ持たない |
| D3 | 引数名 | `ChunkTakeCount` の `suppressEndpointing` を **`behind`** に改名する。末尾無音を止めるだけでなく上限も切り替えるようになり、名前が意味と合わなくなるため |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-10（10 秒 / 遅れているとき 20 秒、上限は常に 20 秒）、REQ-TRX-LIVE-04（確定条件①）、
  REQ-TRX-LIVE-12 / 13（「20 秒分に達していなくても」→「上限に達していなくても」、自ソース分の説明）
- [x] `docs/spec/02_architecture.md` — §5.2 の図の「20秒分たまったら」
- [x] `docs/spec/04_sequence_diagram.md` — §3 の確定条件
- 変更なし: `03_class_diagram.md`（追加した `LiveChunkSamples` は `internal` 定数で、図に載せていない）

## 5. アーキテクチャへの影響

- ADR: **不要**。定数と分岐の追加だけで、層構成・依存方向・スレッドモデルに触れない
- ADR-0007（提案中）の「確定パスは今の仕組みのまま」とは矛盾しない。確定パスの区切り方の値が変わるだけで、2 段構成の前提は同じ

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `LiveChunkSamples`（10 秒分）を追加。`ChunkTakeCount` の①を `behind ? 20 秒 : 10 秒` にし、引数 `suppressEndpointing` を `behind` に改名。関連するコメント |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 上限を前提にしていたテストを 10 秒に合わせ、下のテストを追加 |

## 7. 実装手順

### グループ A — 上限
- [x] **A1** `LiveChunkSamples` を追加し、`ChunkTakeCount` の①を遅れの有無で切り替える
- [x] **A2** 引数を `behind` に改名し、コメントを直す

### グループ B — テスト
- [x] **B1** 既存テストを 10 秒の上限に合わせる
- [x] **B2** 下の 4 件を追加する

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

**変更（上限 20 秒 → 10 秒に合わせる）**
- `ChunkTakeCount_ReachedThreshold_TakesExactlyThreshold` → **`ChunkTakeCount_ReachedLiveChunk_TakesExactlyLiveChunk`** — 遅れていなければ 10 秒分で確定する
- `ChunkTakeCount_FarOverThresholdAndIdle_StillTakesOnlyThreshold` → **`ChunkTakeCount_FarOverLiveChunkAndIdle_StillTakesOnlyLiveChunk`**
- `ChunkTakeCount_OverThresholdWithSilentTail_StillTakesOnlyThreshold` → **`ChunkTakeCount_OverLiveChunkWithSilentTail_StillTakesOnlyLiveChunk`**
- `ChunkTakeCount_ContinuousSupplyWithVoiceAtEnd_TakesNothing` / `ChunkTakeCount_AllSilenceBelowThreshold_TakesNothing` /
  `ChunkTakeCount_SupplyIdleAtThreshold_TakesWholeBuffer` — バッファ長を 10 秒未満に変更（16 秒・19 秒のままだと①が先に成立する）
- `behind: true`（旧 `suppressEndpointing: true`）を渡す 3 件 — 引数名だけ変更

**追加**
- **`ChunkTakeCount_BehindAndOverLiveChunk_WaitsFor20Seconds`** — 遅れているときは 10 秒を超えても切らず、20 秒まで待つ
- **`ChunkTakeCount_BehindAndFarOverThreshold_TakesOnlyThreshold`** — 遅れていても上限は 20 秒分
- **`TakeNextChunk_NotBehindAndTalkingContinuously_TakesLiveChunk`** — 話し続けて 12 秒分たまったら 10 秒分を確定し、2 秒分を残す
- **`TakeNextChunk_BehindAndTalkingContinuously_WaitsFor20Seconds`** — 同じ 12 秒分でも、他ソースと合わせて遅れていれば待つ

> **テストで守れない範囲:** 10 秒入力の処理効率（音声 1 秒あたりの処理時間）は未実測。Zoom 会議中など GPU を取られた状態で、
> 遅れていない間の 10 秒チャンクが追いつくか（遅れが 60 秒に達して 20 秒へ戻る頻度）は、T182 の実機計測（確定の処理能力）で確かめる。

## 9. 未解決の質問

なし（上限の切り替え方と速報の目標は、利用者の選択 (2026-10-03) で決定済み）。

## 10. 前提

- T182 の計測（確定の処理能力 90% 以上など）は、この変更後の確定パスで行う
- 話し続けているときの確定の遅れは、最大で「10 秒 ＋ 1 秒（ポーリング周期）＋ 処理時間」になる
