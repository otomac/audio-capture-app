# T184 — 通常の停止で、処理中チャンクの残りの区間が捨てられる

> **状態:** 進行中 — 2026-10-03
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

通常の停止（打ち切りではない停止）で、停止操作の時点で処理中だったチャンクの残りの区間を捨てない。
REQ-TRX-LIVE-11（滞留分を吐き切る）を、処理中のチャンクについても満たす。

## 2. スコープ境界

**やること**
- `ProcessChunk` の区間ループが停止要求（`_isRunning == false`）で抜けないようにする。抜けるのはキャンセル
  （打ち切り `RequestAbort`・`Dispose`）のときだけ
- 呼び出し元ごとの切り替え（`interruptible`）を無くす

**やらないこと（重要）**
- **`TranscriptionLoop` のポーリング側は変えない。** 停止要求で次のチャンクの取り出しをやめ、排出処理へ移る流れはそのまま
- **T117 の安全策（ワーカー未終了時は `WhisperProcessor` を破棄しない）は変えない**
- **`StopSession` / `RequestAbort` の待ち方（上限なし・打ち切りで 10 秒）は変えない**
- **チャンクの確定条件（REQ-TRX-LIVE-04 / 10 / 12 / 13）は変えない**（T183 の範囲）

## 3. 原因

`TranscriptionLoop` は通常運転中のチャンクを `interruptible: true` で処理し、`ProcessChunk` の区間ループは
`ShouldStopRegionLoop`（`cancelled || (interruptible && !isRunning)`）が真になると抜ける。チャンクは
`TakeNextChunk` でバッファから取り除き済みなので、抜けた後の区間は排出処理でも拾われず `.txt` に届かない。

`interruptible` は T127 の対策で、当時の `StopSession` には 30 秒の猶予があり、全区間を回し切るとそれを超えて
T117 の「破棄見送り」経路に入るのを避けるためだった。T165 で猶予を無くし「捨てない」にした後は、
この打ち切りは REQ-TRX-LIVE-11 に反するだけで、守っているものが無い。

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 区間ループを抜ける条件 | **キャンセル（`token.IsCancellationRequested`）だけ。** 停止要求は見ない |
| D2 | `interruptible` 引数 | **削除する。** 呼び出し元 3 か所（通常運転・排出・末尾）が同じ挙動になるため、切り替える意味が無い |
| D3 | 判定の置き場 | `ShouldStopRegionLoop` を **削除し**、区間ループで `token.IsCancellationRequested` を直接見る。残すと判定が `return cancelled` だけになり、使わない `isRunning` を受け取る関数とそのテストはトートロジーにしかならない。「`_isRunning` を見てはいけない」は `ProcessChunk` の remarks とループ内のコメントで残す |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-04（停止要求で打ち切るのは次のチャンクの取り出し。処理中のチャンクは最後まで）、
  REQ-TRX-LIVE-11（吐き切りに処理中のチャンクの残りを含む）
- [x] `docs/spec/04_sequence_diagram.md` — §4 録音停止に「処理中のチャンクを最後の区間まで処理」を追加
- 変更なし: `02_architecture.md`（スレッドモデルは同じ）、`03_class_diagram.md`（`ProcessChunk` / `ShouldStopRegionLoop` は private / internal で載っていない）

## 6. アーキテクチャへの影響

- ADR: **不要**。層構成・依存方向・スレッドモデルに触れない（ワーカースレッド内の分岐条件だけ）

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `ProcessChunk` / `ProcessChunkCounted` から `interruptible` を削除。`ShouldStopRegionLoop` を削除し、区間ループはキャンセルだけを見る |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `ShouldStopRegionLoop` のテスト 4 件を削除 |

## 8. 実装手順

### グループ A — 区間ループ
- [x] **A1** `ShouldStopRegionLoop` を削除し、区間ループで `token.IsCancellationRequested` を直接見る (`TranscriptionService.cs`)
- [x] **A2** `ProcessChunk` / `ProcessChunkCounted` と呼び出し元 3 か所から `interruptible` を削除し、コメントを直す (`TranscriptionService.cs`)
- [x] **A3** `ShouldStopRegionLoop` のテストを削除する (`TranscriptionServiceTests.cs`)

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§5）の更新反映を読み直す

## 9. テスト一覧

追加なし。削除 4 件（対象の `ShouldStopRegionLoop` が無くなるため）:

- `ShouldStopRegionLoop_Cancelled_AlwaysStops` / `ShouldStopRegionLoop_Running_Continues` — 判定がキャンセルだけになり、関数として切り出す意味が無くなった
- `ShouldStopRegionLoop_StopRequestedWhileInterruptible_Stops` — T127 の挙動そのもので、T184 で逆になる
- `ShouldStopRegionLoop_DrainingAfterStop_DoesNotStop` — `interruptible` が無くなり、通常運転と排出の区別が無い

> **テストで守れない範囲:** `ProcessChunk` は `WhisperProcessor`（モデルが必要）を呼ぶため、
> 「停止後に残りの区間が `.txt` へ書かれる」こと自体はユニットテストで確かめられない。区間ループがキャンセルしか見ないことはコードで読む。
> 実機での確認（停止直前まで話し続けた録音で、最後の発話が `.txt` に残るか）は利用者の手元で行う。

## 10. 未解決の質問

なし。

## 11. 前提

- `StopSession` は打ち切り要求が来るまで上限なしで待つ（T165）。処理中のチャンクを回し切っても停止がタイムアウトしない
- 1 チャンクは最大 20 秒（REQ-TRX-LIVE-10）なので、停止時に増える待ち時間は最大でチャンク 1 つ分。ただし T183 ②のとおり、
  遅れているときはギャップ分割で 20 秒を超えるチャンクができうる。その場合も「捨てない」が仕様であり、待てなければ打ち切りを使う
