# T191 — 確定パスで flash attention を有効にする

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **背景:** [T182](./T182-provisional-pass-measurement.md) の M1 / E6（確定パスの形で 10 秒 −28%、20 秒 −23%）

## 1. 目的

Whisper の flash attention（`WhisperFactoryOptions.UseFlashAttention`。Whisper.net 1.9.0 の既定は無効）を、効く条件でだけ有効にし、
確定パスとファイル文字起こしを速くする。Zoom 会議中に確定パスの余裕がほとんど無いこと（ADR-0007・NFR-11）への手当てである。

## 2. 実測（2026-10-12、この機械: RTX 3050 6GB / Vulkan / 20 コア）

スクラッチの検証プログラムで、T182 の `ma02`（15.8 分の実際の会議）を、アプリと同じ「チャンク → 有声区間（`SplitVoicedRegions`）→ 1 秒への埋め（`PadToMinimum`）→ `ProcessAsync`」で処理した。
アプリと同じランタイムの読み込み順（`LoadModel` を先に 1 度通す）。各条件を無効 → 有効 → 無効 → 有効の順に 2 回ずつ回し、2 回目を採った（結果の文は 2 回とも同じ）。
文字誤り率は T182 と同じ正解（`ma02.ref.corrected.txt`）・同じ正規化（NFKC・文字と数字だけ）。

| 条件 | flash 無効 | flash 有効 | 速さ | 文字誤り率（無効 → 有効） |
|---|---|---|---|---|
| small・GPU・10 秒チャンク（ライブの確定パスの形） | 355.4 秒 | 271.3 秒 | **−23.7%** | 34.72% → 35.30%（+0.58 ポイント） |
| 同上・行頭の付け札を落とした後（T188 の出力と同じ） | | | | 33.78% → 34.13%（+0.35 ポイント） |
| small・GPU・20 秒チャンク（ファイル文字起こしの形） | 266.6 秒 | 201.5 秒 | **−24.4%** | 34.7% → 33.1%（−1.6 ポイント） |
| small・**CPU**・10 秒チャンク（3 分） | 123.9 秒 | 152.9 秒 | **+23.4%（遅くなる）** | （3 分なので正解と比べていない。本文の差 5.4%） |

- 繰り返し・定型の幻聴の行は、GPU・10 秒チャンクで無効 0 / 有効 0。行頭の付け札は 58 / 59 行（T188 で落とす）。
- 本文は 6% 前後入れ替わるが、文字誤り率は 10 秒で悪く・20 秒で良くなり、向きが揃わない。**質は同等**と読む。
- 1 回目の計測の数字（355.4 → 343.9 秒など）も同じ傾向だった。

## 3. スコープ境界

**やること**
- **GPU で動くときだけ** flash attention を有効にする（ライブの確定パス・ファイル文字起こしの 2 つ目のモデル）
- 設定 `UseFlashAttentionOnGpu`（既定 `true`。`settings.json` のみ）で止められるようにする

**やらないこと（重要）**
- **CPU では使わない**（速報は CPU なので使わない。GPU 使用を OFF にしたとき・GPU が無い環境も使わない）
- **区切り方・出力は変えない**

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 有効にする条件 | `UseFlashAttentionOnGpu`（既定 `true`）かつ **GPU で読み込むとき**（`UseGpu = true`）。CPU では 23% 遅くなったため使わない |
| D2 | GPU を頼んだが GPU が使えなかったとき | 読み込み後の判定（REQ-TRX-02 / REQ-GPU-05 の「実際に重みが載った先」）で CPU と分かったら、**flash attention 無しで読み込み直す**。GPU が無い環境で、初回の起動だけ読み込みが 1 回増える（次からは GPU 使用が OFF に倒れているので 1 回で済む。REQ-GPU-02） |
| D3 | ファイル文字起こしの 2 つ目のモデル | ライブ用と同じ規則（`UseGpu` はライブ側の設定に従う。REQ-TRX-FILE-17） |
| D4 | 判定の形 | `internal static bool ShouldUseFlashAttention(bool enabled, bool useGpu)` と `NeedsReloadWithoutFlash(bool flashUsed, bool? gpuInUse)` に切り出してテストする |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-GPU-07（新設）と REQ-CFG-14（新設: `UseFlashAttentionOnGpu`）
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService.UseFlashAttentionOnGpu`、`AppSettings` のキー

## 6. アーキテクチャへの影響

- ADR: 不要 — 推論の設定 1 つ。スレッド・依存方向は変えない。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `LoadModel` / `TryCreateFactory` で flash attention を指定、CPU と分かったら読み込み直す |
| `AudioCaptureApp/Models/AppSettings.cs` | `UseFlashAttentionOnGpu` |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 設定をサービスへ写す |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 判定のテスト |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値 |
| `README.md` | `settings.json` のキーの説明 |

## 8. テスト一覧

- **`ShouldUseFlashAttention_OnlyOnGpuWhenEnabled`** — 有効かつ GPU のときだけ使う
- **`NeedsReloadWithoutFlash_WhenFlashLandedOnCpu`** — flash を頼んで CPU に載ったときだけ読み込み直す（不明なら読み込み直さない）
- **`DefaultValues_AreCorrect`**（改訂）

> **テストで守れない範囲:** 実際の速さと文の質（§2 で測った）。GPU が無い環境での読み込み直し（この機械では起こせない）。

## 9. 未解決の質問

なし（既定を有効にするかは §2 の実測で決めた）。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 505 件成功 / 0 件失敗 / 0 件スキップ（T192 完了時 497 件 + 本タスク 8 件）
- **読み込みの経路（ネイティブログの `flash attn = 0/1` と重みの載った先）:** ライブ用 small・GPU 要求 → `flash attn = 1`・Vulkan0／GPU 使用 OFF → `0`・CPU／速報用 base → `0`・CPU／設定で OFF → `0`・Vulkan0。4 通りとも D1 のとおり
- **実モデルの通し:** 2 音源・速報あり・120 秒でエラー 0 件・速報の遅れ p50 0.53 / p95 1.39 秒
- 計画からの逸脱: なし
- 未確認: D2（GPU を頼んだのに CPU に載ったときの読み込み直し）は、この機械では起こせないため実機で確かめていない
- **見つけた別の問題（T195 で起票）:** 通しの確認で、停止時の吐き切りが長かった（滞留 20 秒に 14.2 秒）。flash attention とは関係なく、T188 の文脈プロンプトが原因だった — 2 音源が同じ音声を拾う条件では、確定パスの費用がほぼ倍（音声 1 秒あたり 254 → 484ms。flash 無効でも 331 → 547ms）になり、1 回 12〜28 秒かかる呼び出しが出て、行も 80 → 69 に減った。1 音源では差が無い（240 → 233ms）
