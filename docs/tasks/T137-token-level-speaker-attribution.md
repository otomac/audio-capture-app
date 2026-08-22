# T137 — 話者判定の重複計算をトークンの発話時間帯に限定する

> **状態:** 完了 — 2026-08-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [T115](./T115-speaker-diarization.md)（本体）／[T141](./backlog.md)（テキスト分割・見送り分）

## 1. 目的

話者の割り当てが、Whisper セグメントの**時間範囲**（最小長パディングで足した無音や、発話後の
余白を含む）で決まっているため、隣の話者の時間帯まで重複に数えて誤った話者へ寄る。
重複計算を**実際に発話が存在する時間帯**だけに限定して、これを減らす。

> **着手時の前提は外れた。** 起票時は「実機で観測された混在 2 件はすべてこれが原因」と
> 判断していたが、実装後の実測でその 2 件は 1 件も変わらなかった。真因は別で、
> Diarization 側の検出漏れと同時発話だった（下の §11）。本タスクが実際に効いたのは
> 断片化の方であり、目的として書いた効果は得られていない。

## 2. スコープ境界

**やること**

- ファイル文字起こしの Diarization 経路で `WithTokenTimestamps()` を有効にする
- トークン時刻から「発話時間帯」を作り、`TranscriptSegment` に持たせる
- `TranscriptDiarizationMerger` の重複計算を、発話時間帯があればその合計で行うようにする
- 発話時間帯が無い場合は従来どおりセグメントの時間範囲で測る（縮退）

**やらないこと（重要）**

- **テキストの分割はしない。** T141 として切り出した。Whisper の BPE トークンが
  マルチバイト文字の途中で切れると `WhisperToken.Text` の時点で U+FFFD へ置換され、
  **元のバイトを復元できない**（実測で 18 セグメント中 9 件が該当）。
- **DTW（`UseDtwTimeStamps`）は使わない。** D2 参照。
- **ライブ文字起こしの経路には触れない。** `WithTokenTimestamps()` はファイル文字起こしの
  Diarization 経路にだけ付ける。ライブ側の推論コストと挙動を変えない。
- **Diarization 無効時の経路には触れない。** 従来の出力と 1 文字も変えない。
- **話者クラスタリングの設定（閾値・モデル）は変えない。** それは T138。

## 3. 決定事項

| # | 選択が必要だった点 | 結論 |
|---|---|---|
| D1 | テキストも分割するか | **しない。** U+FFFD による文字化けのリスクに対し、対象は [低] の事象のみ。T141 へ分離 |
| D2 | DTW を使うか | **使わない。** 有効化にはモデルごとの alignment heads プリセットが要るが、本アプリはモデルパスを設定で差し替えられる（実機は `ggml-large-v3-turbo-q5_0.bin` で、`Small` 決め打ちは誤り）。実測で DTW なしでも時刻は単調かつ妥当 |
| D3 | 発話時間帯をどこで作るか | **`TranscriptionService`。** sherpa-onnx 側にも Merger 側にも Whisper 固有の型を出さない |
| D4 | Merger の受け取り方 | **`TranscriptSegment` に省略可能なリストとして持たせる。** 既存 19 件のテストが無改修で通ることを不変条件にする |
| D5 | 特殊トークンの除外 | **`[_` で始まり `]` で終わるものを除く。** `WhisperToken` に判別用のメンバーが無いため（`Id` の閾値はモデル依存で当てにできない） |
| D6 | 長さ 0 のトークン | **除く。** 重複に寄与しないうえ、前後のトークンが同じ時間帯を覆う |
| D7 | トークンが 1 つも無い場合 | **セグメントの時間範囲へ縮退する。** 話者不明を増やさない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — `REQ-TRX-DIA-13` を新設。`REQ-TRX-DIA-05` に発話時間帯の但し書き。§13 に「割り当ての粒度」の制約注記
- [x] `docs/spec/03_class_diagram.md` — `SpeechSpan` を追加、`TranscriptSegment` に `SpeechSpans`
- [x] `docs/spec/04_sequence_diagram.md` — §6.1 の③④に発話時間帯の生成と使用を追記
- 変更なし: `02_architecture.md`（層構成・依存方向・スレッドモデルは不変）

## 5. アーキテクチャへの影響

**ADR 不要。** 層構成・依存方向・スレッドモデルのいずれも変わらない。新しい外部ライブラリも
入れない（`WithTokenTimestamps()` は導入済みの Whisper.net の既存 API）。
[ADR-0003](../adr/0003-speaker-diarization-with-sherpa-onnx.md) が定めた
「両エンジンを独立させ、統合は純粋関数が担う」構造もそのままである。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Models/TranscriptSegments.cs` | `SpeechSpan` を追加。`TranscriptSegment` に `SpeechSpans` を追加 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | Diarization 経路の builder に `WithTokenTimestamps()`。トークンから発話時間帯を組む `BuildSpeechSpans` を追加 |
| `AudioCaptureApp/Services/TranscriptDiarizationMerger.cs` | 重複計算を発話時間帯の合計に切り替える（無ければ従来どおり） |
| `AudioCaptureApp.Tests/TranscriptDiarizationMergerTests.cs` | 発話時間帯ありのケースを追加 |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `BuildSpeechSpans` のテストを追加 |

## 7. 実装手順

### グループ A — Model
- [x] **A1** `SpeechSpan` を追加し、`TranscriptSegment` に `SpeechSpans` を持たせる

### グループ B — Merger（先に書く。純粋関数なのでテストで固められる）
- [x] **B1** 重複計算を「発話時間帯があればその合計」に切り替える
- [x] **B2** 発話時間帯の妥当性検証（`End < Start` を拒否）を足す
- [x] **B3** テストを追加（余白が隣の話者にかかる場合／縮退／不正値）

### グループ C — 発話時間帯の生成
- [x] **C1** `BuildSpeechSpans`（特殊トークン・長さ 0 を除き、重なりを結合する `internal static`）を追加
- [x] **C2** テストを追加（特殊トークン除去・結合・空入力）
- [x] **C3** `CollectTranscriptSegmentsAsync` に `WithTokenTimestamps()` と結線

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

`TranscriptDiarizationMergerTests`

- **`Merge_SpeechSpans_IgnoresTrailingPaddingOutsideSpeech`** — セグメント末尾の余白が隣の話者に
  かかっていても、発話時間帯だけで測れば正しい話者を選ぶこと（本タスクの核心）
- **`Merge_SpeechSpans_SumsMultipleSpans`** — 複数の発話時間帯の合計で比較すること
- **`Merge_SpeechSpans_Empty_FallsBackToSegmentRange`** — 空なら従来どおりセグメント範囲で測ること
- **`Merge_SpeechSpans_Null_FallsBackToSegmentRange`** — 未指定でも同上（既存 19 件が通る根拠）
- **`Merge_SpeechSpanEndBeforeStart_Throws`** — 不正な発話時間帯を拒否すること

`TranscriptionServiceTests`

- **`BuildSpeechSpans_ExcludesSpecialTokens`** — `[_BEG_]` / `[_TT_nnn]` を除くこと
- **`BuildSpeechSpans_ExcludesZeroLengthTokens`** — 長さ 0 を除くこと
- **`BuildSpeechSpans_MergesOverlappingAndAdjacent`** — 重なり・隣接をまとめること
- **`BuildSpeechSpans_AppliesOffset`** — 区間先頭のオフセットを足すこと
- **`BuildSpeechSpans_NoUsableTokens_ReturnsEmpty`** — 縮退の入口を作ること

> **テストで守れない範囲:** `WithTokenTimestamps()` が返す実際のトークン時刻の精度。
> Whisper モデルが要るためユニットテストにできない。実機で確認する。
> `BuildSpeechSpans` は Whisper の型に依存しない形（時刻とテキストの組）で切り出してテストする。

## 9. 未解決の質問

なし（テキスト分割の可否は D1 で決着。T141 へ分離済み）。

## 10. 前提

- Whisper.net 1.9.0 の `WithTokenTimestamps()` が、処理対象音声の先頭を基準とした
  トークン時刻を 10ms 単位で返す（実測で確認済み）。
- トークン時刻は概ね単調である（実測で確認済み。逆転しても結合処理が吸収する）。

---

## 11. 実測で分かったこと（着手時の前提の誤り）

実機の音声（2 分 42 秒 / 58 セグメント）で、重複計算を (a) セグメント範囲 と
(b) 発話時間帯 で比較した。正解は利用者の申告（A = 牛尾さん / B = 司会）。

| 指標 | (a) 従来 | (b) 本タスク |
|---|---|---|
| A の断片数 | 6 | 6 |
| B の断片数 | 3 | **2** |
| 混在（同一 ID が別人に跨る） | 2 | **2（変わらず）** |

判定が変わったのは 2 セグメントだけで、いずれも改善方向だった。

- `#15 15:08:28` 話者6 → **話者1**（正解 A。断片が本体 ID へ統合された）
- `#35 15:09:31` 話者21 → **話者8**（正解 B。同上）

**混在 2 件が動かなかった理由。** Diarization が返した話者区間を該当箇所で直接確認したところ、
問題は割り当ての粒度ではなかった。

| 箇所 | Diarization の実際の出力 | 真因 |
|---|---|---|
| `#8` 15:08:16-17「えー、どれまでですか?」 | `15:08:15.03-15:08:28.97 話者1` の一本のみ。相槌に対応する区間が**存在しない** | **検出漏れ。** 切り替え先の区間が無いので、粒度をいくら細かくしても直らない |
| `#13` 15:08:24-25「へぇー」 | 話者1（13.9 秒）に加えて `15:08:25.12-25.44 話者4`（0.32 秒）が**重なっている** | **同時発話。** 重複長で選ぶ限り長い方が必ず勝つ |
| `#50` 15:10:00-02 | `話者30`（〜15:10:01.51）と `話者8`（15:09:59.97〜）が**重なっている** | 同上 |

真因は T142 として起票した。**テキスト分割（T141）も同じ理由でこの 3 件には効かない** —
切り分ける先の話者境界が無い、または重なっているためである。T141 の前提も書き直した。

## 実行結果 (2026-08-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 190 件成功 / 0 件失敗 / 0 件スキップ（本タスクで追加 20 件）
- 既存の Merger テスト 19 件は**無改修で通過**（発話時間帯が未指定なら従来動作へ縮退することの裏付け）

### 計画からの逸脱

1. **目的として書いた効果は得られていない。** §11 のとおり。実装・テストは計画どおりだが、
   狙った混在の解消には至らず、副次的に断片化が 1 つ減っただけである。
   残す判断は利用者が行った（測り方として素直に正しく、真因への対処とは独立して残せるため）。
2. **`EndsWith(']')` を char 版にした（計画外）。** CA1865 が string 版を拒否したため。
