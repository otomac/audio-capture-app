# T129 — ライブ文字起こしの確定契機に「末尾無音」を追加する

> **状態:** 完了 — 2026-08-21
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ライブ文字起こしの出力粒度が実質 20 秒固定で、「発話が終わった」という確定契機が存在しない。
発話終了に同期した粒度で出力し、発話から画面表示までの遅延を 0〜20 秒（＋推論時間）から
数秒へ縮める。

## 2. スコープ境界

**やること**

- 確定契機に「バッファ末尾に一定長の無音が積まれたら確定する」（エンドポイント検出）を追加する
- 既存の滞留契機（T120）を「バッファ先頭サンプルの滞留時間」から「供給の途絶時間」へ定義し直し、
  閾値を 20 秒 → 5 秒にする
- 上記に伴う仕様書（`01_requirements.md` / `04_sequence_diagram.md`）の更新

**やらないこと（重要）**

- **ギャップ分割（`ShouldSplitOnGap`、500ms）には触らない。** 供給が*再開したとき*の契機であり、
  供給が止まったままの契機（REQ-TRX-LIVE-12）とは役割が違う。両方要る
- **リサンプラ／LPF の状態リセット条件を変えない。** 末尾無音での確定は音声が地続きなので
  リセットしない。リセットするのはギャップ分割だけ（REQ-TRX-LIVE-09）
- **`interruptible` と停止時の排出経路に触らない**（T126 / T127 の対策をそのまま維持する）
- **ポーリング周期（1 秒）を変えない。** 遅延の主因ではなく、変えると走査頻度と停止応答へ波及する
- **`SilenceCutOptions` の既定値・クランプ範囲を変えない**
- **チャンク長の上限 20 秒（`BufferThresholdSamples`）を変えない**

## 3. 決定事項

実装中に蒸し返さない。変更するときは本セクションを直してから。

| # | 決定 | 結論 |
|---|---|---|
| D1 | 出力粒度の解決方式 | **末尾無音での確定（エンドポイント検出）を追加する。** チャンク長の短縮は却下（発話の途中で機械的に切れて語が壊れ、連続発話中の推論回数が 4 倍になる） |
| D2 | 末尾無音の保持時間 | **`SilenceCut.MergeGapSeconds`（既定 2.0 秒）と同じ値を使い、独立した定数を新設しない。** この値が REQ-TRX-09 ③で「発話の切れ目」を定義している値そのものであり、揃えれば確定チャンクは有声区間ちょうど 1 個を含む形になる（1 発話が複数行に割れず、Whisper 呼び出し回数も現状据え置き）。定数を分けると、短い側なら「息継ぎで割れる＋推論増」、長い側なら「確定が遅れる」のどちらかが必ず起きる |
| D3 | 滞留契機（旧 `StaleBufferAge`）の扱い | **「供給の途絶時間」へ定義し直したうえで 5 秒にする。** 滞留時間のまま 5 秒へ下げると、連続供給下では `bufferAge ≈ バッファ長` なので D1 で却下したチャンク長短縮と同じ挙動になる |
| D4 | 定数名 | **`StaleBufferAge` → `StaleSupplyIdle`。** 測る対象が変わったので名前も変える |
| D5 | 末尾無音の判定窓 | **バッファ末尾に揃えて敷き、先頭側の端数も実長で 1 窓として評価する。** 先頭揃えだと末尾の最大 99ms が端数窓に紛れて判定がぶれる。端数を飛ばすと、発話がバッファ先頭の端数に収まっている場合に「全体が無音」と誤判定して確定を取りこぼす |
| D6 | 全体が無音のバッファ | **確定しない。** 従来どおり 20 秒上限で切り出され、有声区間 0 件として Whisper を呼ばずに捨てられる（REQ-TRX-09） |
| D7 | `ChunkTakeCount` の形 | **純関数のまま引数を差し替える**（`bufferAge` を削り、`supplyIdle` / `trailingSilenceSamples` / `endpointSilenceSamples` を足す）。ユニットテスト可能性を落とさない |
| D8 | `MergeGapSeconds` が 0 のとき | **末尾無音が 0 サンプルなら確定しない。** 0 を設定されると「有声のまま毎ポーリング確定」になり、発話の途中で切れる。実装中に気づいた分岐で、仕様（REQ-TRX-LIVE-13）へも追記した |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - `REQ-TRX-LIVE-04` — 確定条件が 3 つあること、その優先順（上限が最優先）を明記
  - `REQ-TRX-LIVE-12` — 「先頭サンプルの滞留」→「供給の途絶」へ定義し直し、20 秒 → 5 秒、
    実装箇所を `StaleSupplyIdle` へ。滞留時間で測ってはならない理由を残す
  - `REQ-TRX-LIVE-13`（新規）— 末尾無音での確定。窓の敷き方（D5）、保持時間を
    `MergeGapSeconds` と同値にする根拠（D2）、全無音時に確定しないこと（D6）
- [x] `docs/spec/04_sequence_diagram.md` — ライブ文字起こしのポーリング行を
  「20秒分(閾値)に到達したソースを検出」→ 3 つの確定条件に書き換え
- [x] `docs/spec/02_architecture.md` — 影響なし（層構成・スレッドモデル・データフローは不変）
- [x] `docs/spec/03_class_diagram.md` — 影響なし。`TrailingSilenceSamples` は
  `ChunkTakeCount` / `ShouldSplitOnGap` / `ChunkStartElapsed` と同格の内部ヘルパーであり、
  これらは既に図に載せていない（「要件理解に必要なメンバーに絞る」— 50-spec-standards §3）

## 5. アーキテクチャへの影響

`TranscriptionService`（Services 層）の内部のみ。層構成・依存方向・スレッドモデル
（`WhisperTranscription` ワーカー 1 本 ＋ `_sources` ごとの `BufferLock`）は変わらない。
新規パッケージも増やさない。

- ADR: **不要**（ADR-0001 の 3 層構成・非抽象化方針に一切触れない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TrailingSilenceSamples` を新設。`ChunkTakeCount` のシグネチャと判定を差し替え。`StaleBufferAge` を `StaleSupplyIdle`（5 秒）へ改名・再定義。`TakeNextChunk` が新しい入力を組み立てて渡す |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `TrailingSilenceSamples` のテストを追加。既存の `ChunkTakeCount_*` 7 件を新シグネチャへ書き換え、契機ごとのテストを追加 |
| `docs/spec/01_requirements.md` | §4 のとおり（済） |
| `docs/spec/04_sequence_diagram.md` | §4 のとおり（済） |
| `docs/tasks/backlog.md` | T129 を `[~]` へ（済）、作業中に見つけた仕様書の不整合 T131 / T132 を起票（済） |

## 7. 実装手順

上から順に実行する。グループごとにビルドを通す。

### グループ A — 判定ロジック

- [x] **A1** `StaleBufferAge` を `StaleSupplyIdle`（`TimeSpan.FromSeconds(5)`）へ改名し、
  XML コメントを「供給が途絶えている時間」の定義へ書き直す。滞留時間で測ってはならない理由を
  残す (`AudioCaptureApp/Services/TranscriptionService.cs`)
- [x] **A2** `TrailingSilenceSamples(IReadOnlyList<float> buffer, double rmsThreshold)` を新設する。
  末尾から `SilenceWindowSamples`（1600）ごとに遡り、最初の有声窓に当たるまでの無音サンプル数を返す。
  有声窓が 1 つも無ければ `null`。先頭側の端数窓も実長で評価する。RMS の式は
  `CollectVoicedWindows` と同一 (`TranscriptionService.cs`)
- [x] **A3** `ChunkTakeCount` を
  `(int bufferedSampleCount, TimeSpan supplyIdle, int? trailingSilenceSamples, int endpointSilenceSamples)`
  へ差し替え、①上限 ②末尾無音 ③供給途絶 の順で判定する (`TranscriptionService.cs`)
- [x] **A4** `TakeNextChunk` から `supplyIdle`（`nowElapsed - state.BufferEndElapsed`）と
  末尾無音サンプル数、`SecondsToSamples(SilenceCut.MergeGapSeconds)` を渡す。
  `SilenceCutOptions` は引数で受け取り `static` を維持する。呼び出し元（`TranscriptionLoop`）も合わせる
  (`TranscriptionService.cs`)

### グループ B — テスト

- [x] **B1** `TrailingSilenceSamples` のテストを追加する（§8）
  (`AudioCaptureApp.Tests/TranscriptionServiceTests.cs`)
- [x] **B2** 既存の `ChunkTakeCount_*` 7 件を新シグネチャへ書き換える。
  `StaleBufferAge` を直接参照している `ChunkTakeCount_ExactlyAtStaleAge_TakesWholeBuffer` は
  `StaleSupplyIdle` 基準へ読み替える (`TranscriptionServiceTests.cs`)
- [x] **B3** 契機ごとのテストを追加する（§8）(`TranscriptionServiceTests.cs`)

### グループ Z — 検証（必須・最後に置く）

- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

**`TrailingSilenceSamples`**

- **`TrailingSilenceSamples_AllSilence_ReturnsNull`** — 全体が無音なら `null`（＝確定しない）
- **`TrailingSilenceSamples_EmptyBuffer_ReturnsNull`** — 空バッファでも例外にならず `null`
- **`TrailingSilenceSamples_EndsWithVoice_ReturnsZero`** — 有声で終わるなら末尾無音は 0
- **`TrailingSilenceSamples_SilentTail_ReturnsTailLength`** — 末尾の連続無音長を返す
- **`TrailingSilenceSamples_LengthNotWindowMultiple_CountsFromEnd`** — 窓長の倍数でない長さでも
  末尾揃えで数える（先頭揃えとの差が出る長さで検証する）
- **`TrailingSilenceSamples_VoiceOnlyInHeadRemainder_NotAllSilence`** — 発話が先頭側の端数窓に
  しか無くても「全体が無音」と誤判定しない（D5）

**`ChunkTakeCount`**

- **`ChunkTakeCount_ReachedThreshold_TakesExactlyThreshold`** — 上限契機。20 秒分ちょうどを取る
- **`ChunkTakeCount_OverThresholdWithSilentTail_StillTakesOnlyThreshold`** — 末尾無音が成立して
  いても上限契機が優先される
- **`ChunkTakeCount_SilentTailAtEndpointLength_TakesWholeBuffer`** — 末尾無音がちょうど保持時間なら
  バッファ全部を取る
- **`ChunkTakeCount_SilentTailOneSampleShort_TakesNothing`** — 1 サンプル足りなければ取らない
- **`ChunkTakeCount_AllSilenceBelowThreshold_TakesNothing`** — 全無音（`null`）では確定しない（D6）
- **`ChunkTakeCount_SupplyIdleAtThreshold_TakesWholeBuffer`** — 供給途絶がちょうど 5 秒なら取る
- **`ChunkTakeCount_SupplyIdleButShorterThanTailMinimum_TakesNothing`** — 0.2 秒未満の断片は取らない
- **`ChunkTakeCount_ContinuousSupplyWithVoiceAtEnd_TakesNothing`** — 供給継続中・末尾に無音なし・
  20 秒未満ならどの契機も成立しない
- **`ChunkTakeCount_EmptyBuffer_TakesNothing`** — 空バッファでは取らない

> **テストで守れない範囲:** 「発話終了から画面表示までが実際に何秒か」はユニットテストでは測れない。
> `WhisperProcessor` は実モデルを要求するため、`TranscriptionLoop` を通した end-to-end の検証も
> 書かない。遅延そのものは §10 の実機確認で押さえる。

## 9. 未解決の質問

なし（D1〜D8 で確定済み）。

## 10. 前提

- マイク（WASAPI 共有モードのキャプチャ）は無音時もサンプルを供給し続ける。この前提が崩れると
  末尾無音の契機は発火せず、REQ-TRX-LIVE-12（供給途絶・5 秒）が拾う形になる
- 1 回の Whisper 推論コストは入力長にほぼ依存しない（whisper.cpp が mel を 30 秒窓へパディングする
  ため）。D2 の「呼び出し回数が変わらないなら総コストも変わらない」はこれに依る
- `SilenceCut.MergeGapSeconds` は既定 2.0 秒。`settings.json` で大きく変えると確定も同じだけ遅くなる
  （意図した連動。D2）

### 実機確認（Z3 の後に行う）

発話・数秒の間・発話…を繰り返す 60 秒程度の録音を行い、次を確認する。

1. 各発話の終了から概ね 3〜4 秒以内に文字起こし表示ウィンドウへ出ること
2. 1 つの発話が複数行に割れていないこと
3. 停止後の残バッファ処理でまとめて出る行が「最後の 1 発話」だけであること
4. `.txt` の行が時刻順に並んでいること

---

## 実行結果 (2026-08-21)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 132 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: D8（`MergeGapSeconds` が 0 のときに末尾無音 0 サンプルで確定しない）を実装中に追加した。
  §3 の決定事項と `REQ-TRX-LIVE-13` の両方へ反映済み。
- 実機確認: §10 の 4 項目すべて OK（発話終了から表示まで 3〜4 秒以内 / 1 発話が複数行に割れない / 停止後にまとめて出る行は最後の 1 発話だけ / `.txt` が時刻順）。マイク入力を伴うため自動テストでは代替できない。
