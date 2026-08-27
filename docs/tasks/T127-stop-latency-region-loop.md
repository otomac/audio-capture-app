# T127 — 停止時に区間ループを打ち切れるようにする

> **状態:** 完了 — 2026-08-20
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **ブランチ:** `feature-silence-cut-before-whisper`（T112 と同じブランチ。依頼により同一 PR に含める）

## 1. 目的

T112 で 1 チャンクが複数の有声区間に分かれるようになり、**1 チャンクあたりの Whisper 呼び出しが
最大 10 回**になった。停止要求が来ても `ProcessChunk` の区間ループは `CancellationToken` しか見て
いないため、`StopSession` の猶予 30 秒の間じゅう全区間を回し切ってしまう。

猶予を超えると `_cts.Cancel()` → さらに 10 秒待ち → それでもワーカーが終わらなければ
`DisposeProcessorSafely(workerExited: false)` の「破棄を見送る」経路に入る。
これは **T117 が直した症状そのもの**であり、T112 が持ち込んだ退行リスクを閉じる。

## 2. なぜ `_isRunning` を無条件に見てはいけないか

停止時の残バッファ排出（`TranscriptionLoop` 終端の drain と tail）は、**`_isRunning` が false の
状態で走る**。区間ループで無条件に `_isRunning` を見ると、排出すべき最後のチャンクを
1 区間も処理せずに捨ててしまい、T120 の書き出し遅延対策が無効になる。

したがって呼び出し元ごとに切り替える。

| 呼び出し元 | `interruptible` | 理由 |
|---|---|---|
| ポーリングループ（通常運転） | `true` | 停止要求で区間の切れ目から抜ける |
| 停止時の drain | `false` | ここは `_isRunning == false` で走る。打ち切ると取りこぼす |
| 停止時の tail | `false` | 同上 |

打ち切りたい場合は `token` をキャンセルする（`StopSession` の 2 段目）。これは `interruptible` に
関わらず常に効く。

## 3. 最悪ケースの見積もり

区間は 2.0 秒以上離れ、各区間は 0.2 秒以上あるため、20 秒チャンクに入る区間数の上限は
`(n-1)×32000 + n×3200 ≤ 320000` より **10**。停止時は「処理中チャンク 1 つ ＋ ソース 2 系統の tail」
なので最悪 **30 回**の推論。1 回 1 秒なら 30 秒で、猶予とちょうど同じ。
本修正により、通常運転中のチャンクは区間の切れ目で抜けるようになる。

## 4. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `ProcessChunk` に `interruptible` 引数を追加、判定を `ShouldStopRegionLoop` に切り出し、3 つの呼び出し元を振り分け |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `ShouldStopRegionLoop_*` 4 件を追加 |

`CancellationToken` は最後の引数でなければならない（CA1068）ため、引数順は
`(chunk, state, interruptible, token)` とした。

## 5. テスト一覧

- **`ShouldStopRegionLoop_Cancelled_AlwaysStops`** — キャンセル済みなら `interruptible` に関わらず打ち切る
- **`ShouldStopRegionLoop_Running_Continues`** — 通常運転中は続行する
- **`ShouldStopRegionLoop_StopRequestedWhileInterruptible_Stops`** — 通常運転中の停止要求で打ち切る
- **`ShouldStopRegionLoop_DrainingAfterStop_DoesNotStop`** — 排出処理では打ち切らない（取りこぼし防止）

> **テストで守れない範囲:** 実際の停止レイテンシはスレッドと Whisper 推論に依存するため
> ユニットテストでは測れない。**手動確認が必要**：発話の多い録音を長めに行い、
> 停止が 30 秒以内に完了して「Cannot dispose while processing」等が出ないこと。
> 判定ロジック自体は純粋関数として切り出し、上記 4 件で固定した。

## 6. アーキテクチャへの影響

なし。private メソッドの引数追加のみ。ADR 不要。

---

## 実行結果 (2026-08-20)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**（exit 0）
- `dotnet test AudioCaptureApp.slnx -c Debug` : **96** 件成功 / **0** 件失敗 / **0** 件スキップ
  （T112 完了時点の 92 件 ＋ 新規 4 件）
- 計画からの逸脱: CA1068 により引数順を `(chunk, state, interruptible, token)` へ変更。
  アナライザーの抑制は行っていない。
