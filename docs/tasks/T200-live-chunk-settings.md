# T200 — ライブの区切りをライブ専用の設定にし、既定を 6 秒 / 0.8 秒にする（small-q5_1 を推奨として案内）

> **状態:** 完了 — 2026-11-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [ADR-0009](../adr/0009-single-pass-light-model-live-transcription.md)（承認 2026-11-23）/ [T199](./T199-light-model-single-pass.md)（実測）/ [T201](./T201-provisional-default-off.md)（同じブランチで行う。利用者の指示 2026-11-23）

## 1. 目的

ADR-0009 案 A の実装。ライブ文字起こしの確定パスの区切り（遅れていないときの上限・発話終了の無音）を
ライブ専用の設定にし、既定を T199 で合格した 6 秒 / 0.8 秒にする。あわせて small-q5_1 を推奨モデルとして案内する。

## 2. スコープ境界

**やること**
- 遅れていないときの上限（今は定数 `LiveChunkSamples` = 10 秒）を設定 `LiveChunkMaxSeconds`（既定 6.0）にする
- ライブの発話終了の無音（今は `SilenceMergeGapSeconds` 2.0 秒と共用）を設定 `LiveEndpointSilenceSeconds`（既定 0.8）にする
- 2 つの値を手編集の不正値に強い `LiveChunkOptions`（`SilenceCutOptions` と同じクランプ）にまとめ、`TranscriptionService.LiveChunking` で受ける
- 推奨モデル（small-q5_1）を設定ウィンドウのライブ用モデルの見出し・モデル管理ダイアログの注記・README で案内する
- 仕様書（01 / 03 / 04 / 05、02 のデータフロー図のラベル）を先に直す

**やらないこと（重要）**
- **遅れているときの扱いは変えない**（上限 20 秒・早期確定の抑止。REQ-TRX-LIVE-10 / 13）
- **ファイル文字起こしと、チャンク内の区間分け（`SplitVoicedRegions`）の `SilenceMergeGapSeconds`（2.0 秒）は変えない** — T199 の D4 もチャンク内の区間分けには 2.0 秒を渡して測った
- **`audio_ctx`（1500）・GPU 推論が `WhisperTranscription` の 1 本の列であること・スレッド構成は変えない**
- **モデルの既定パス（REQ-CFG-04 の `ggml-small.bin`）は変えない。モデルを自動で入手・登録しない**（案内だけ）
- **ギャップ分割（REQ-TRX-LIVE-07）の 20 秒は変えない**
- 速報の既定 OFF は T201 で行う（同じブランチ・別の票）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 設定のキー名と範囲 | **`LiveChunkMaxSeconds`（既定 6.0、許容 2〜20）と `LiveEndpointSilenceSeconds`（既定 0.8、許容 0〜20）。** 上限を 20 にするのは、遅れているときの上限（20 秒）より長くしないため。下限 2 は、それより短いと 1 回の呼び出しが割高で（REQ-TRX-LIVE-13 の実測）、最小長の補い（1 秒）と比べても意味が薄いため。無音は `SilenceMergeGapSeconds` と同じ 0〜20。非有限値は既定へ倒す |
| D2 | 新しい無音を効かせる範囲 | **確定の契機（`ChunkTakeCount` の②）だけ。** チャンク内の区間分けは `SilenceMergeGapSeconds` のまま（T199 の D4 と同じ形。ファイル文字起こしと同じ値を保つ） |
| D3 | UI | **設定ウィンドウに出さず、`settings.json` の手編集だけ**（REQ-CFG-06 と同じ扱い。台帳の既定案） |
| D4 | 既存の `settings.json` | **キーが無ければ新しい既定（6 秒 / 0.8 秒）を当てる**（台帳の既定案）。書き換えの処理は足さない（キーが無ければ既定値で読まれる） |
| D5 | 型の置き場 | **`Services/LiveChunkOptions.cs` に `sealed record` を新設**（`ProvisionalOptions.cs` と同じ並び）。`TranscriptionService.cs` は 2,300 行を超えているため足さない（T193） |
| D6 | 渡し方 | **`TakeNextChunk` に `LiveChunkOptions` を足し、`ChunkTakeCount` には上限のサンプル数を引数で渡す。** 定数 `LiveChunkSamples` は消す |
| D7 | 推奨モデルの案内 | **設定ウィンドウの見出しを「Whisper モデル（ライブ文字起こし。推奨: small-q5_1）」にし、モデル管理ダイアログに入手先の注記を 1 行足す。README の入手手順にも書く。** 速報用の「（推奨: base）」と同じ書き方 |
| D8 | 反映の時期 | **起動時に `MainViewModel` が設定から写す**（`SilenceCut` と同じ）。区切りはポーリングのたびに読むので、録音中に変える経路は無い（UI が無い） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-04 / 10 / 13（上限と発話終了の無音を設定値に）、REQ-CFG-15 を新設（2 つの設定）、REQ-CFG-06（ライブの発話終了の判定には使わないことを明記）、REQ-CFG-08 / REQ-MODELWIN-07 / REQ-MODELWIN-09（新設。推奨モデルの案内）、REQ-TRX-10（「20 秒チャンク」の記述を現状に）
- [x] `docs/spec/02_architecture.md` — §5 データフロー図のラベル（10 秒 → 上限）
- [x] `docs/spec/03_class_diagram.md` — `AppSettings` の 2 メンバー、`LiveChunkOptions`、`TranscriptionService.LiveChunking`
- [x] `docs/spec/04_sequence_diagram.md` — 確定条件のラベル（10 秒 / 2 秒 → 6 秒 / 0.8 秒）
- [x] `docs/spec/05_transcribe_logic.html` — 確定の区切りの値（表・本文・図 2 を 6 秒 / 0.8 秒で描き直す）、モデルの表に small-q5_1 を足す

## 5. アーキテクチャへの影響

- ADR: 不要（ADR-0009 で承認済み。層構成・依存方向・スレッドモデルは変えない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/LiveChunkOptions.cs` | 新設。`MaxChunkSeconds` / `EndpointSilenceSeconds` のクランプと既定 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `LiveChunkSamples` を消し、`LiveChunking` プロパティを足す。`TakeNextChunk` / `ChunkTakeCount` が設定の上限と無音を使う |
| `AudioCaptureApp/Models/AppSettings.cs` | `LiveChunkMaxSeconds` / `LiveEndpointSilenceSeconds` を足す |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 起動時に `LiveChunking` へ写す |
| `AudioCaptureApp/SettingsWindow.xaml` | ライブ用モデルの見出しに推奨を足す |
| `AudioCaptureApp/WhisperModelsWindow.xaml` | 推奨モデルの注記を 1 行足す |
| `README.md` | 入手するモデルに small-q5_1 を書く |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 既存の区切りのテストを新しい既定に合わせ、設定値が効くテストを足す |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値・往復のテストに 2 つの設定を足す |
| `AudioCaptureApp.Tests/LiveChunkOptionsTests.cs` | 新設。クランプ |

## 7. 実装手順

### グループ A — 仕様
- [x] **A1** §4 の仕様書を直す

### グループ B — 実装
- [x] **B1** `LiveChunkOptions` を作る (`Services/LiveChunkOptions.cs`)
- [x] **B2** `TranscriptionService` の区切りを設定値にする (`Services/TranscriptionService.cs`)
- [x] **B3** `AppSettings` に 2 つの設定を足し、`MainViewModel` で写す
- [x] **B4** 推奨モデルの案内（`SettingsWindow.xaml` / `WhisperModelsWindow.xaml` / `README.md`）
- [x] **B5** テスト

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`ChunkTakeCount_*`（既存）** — 上限を引数で渡す形にし、既定（6 秒 / 0.8 秒）で今までと同じ契機の優先順を保つ
- **`LiveChunkOptions_Default_IsSixSecondsAndPointEight`** — テスト内の定数（上限・無音）が既定値と一致する
- **`ChunkTakeCount_LimitArgumentIsUsed`** — 上限は引数の値で切り出す（定数ではない）
- **`ChunkTakeCount_LimitOverThreshold_StillCapsAt20Seconds`** — 遅れていなくても 1 回の呼び出しは 20 秒分まで
- **`ChunkTakeCount_NonPositiveLimit_DoesNotTakeEmptyChunks`** — 上限に 0 が渡っても 0 サンプルのチャンクを返し続けない
- **`TakeNextChunk_NotBehindAndTalkingContinuously_TakesLiveChunk`（変更）** — 話し続けて 12 秒たまったら既定の 6 秒で切る
- **`TakeNextChunk_CustomLimit_TakesConfiguredLength`** — 上限 10 秒を設定すれば 10 秒で切る（ADR-0007 の構成に戻せる）
- **`TakeNextChunk_SilenceShorterThanLiveEndpoint_Waits`** — 0.5 秒の無音では既定の 0.8 秒に届かず確定しない
- **`TakeNextChunk_EndpointUsesLiveSettingNotMergeGap` / `TakeNextChunk_LongerLiveEndpoint_Waits`** — 発話終了は `LiveEndpointSilenceSeconds` で判定し、`SilenceMergeGapSeconds` は見ない
- **`LiveChunkOptionsTests`（新設）** — 既定は 6 / 0.8、範囲内は保つ、範囲外はクランプ、非有限値は既定
- **`AppSettingsTests`（変更）** — 2 つの設定の既定・JSON の往復・キーが無い古い設定でも新しい既定で読む

> **テストで守れない範囲:** 実際の会議での遅れと文字誤り率（T199 の B0 として、この後に利用者が試す）。設定ウィンドウとモデル管理ダイアログの文言の見た目。

## 9. 未解決の質問

台帳の既定案を採った（D3 / D4）。利用者の指示（2026-11-23「T200, T201 を 1 つのブランチでまとめて対応して」）で、既定案のまま進める。

## 10. 前提

- T199 の測り方（D3 / D4）とアプリの実装が同じ形であること — 上限は「遅れていないときだけ」、無音は「確定の契機だけ」に効く

---

## 実行結果 (2026-11-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 527 件成功 / 0 件失敗 / 0 件スキップ（着手前 507 件。T200 で 19 件・T201 で 1 件を追加）
- 図 2（`05_transcribe_logic.html`）はヘッドレスの Edge で画像にして、区切りと寸法線の位置を目で確かめた
- 計画からの逸脱:
  - `ChunkTakeCount` は上限を `MinWhisperSamples`（1 秒）〜 20 秒に収めてから使う（`LiveChunkOptions` で 2〜20 秒に収めてあるが、0 以下が渡ったときに 0 サンプルのチャンクを返し続けないため）。テスト `ChunkTakeCount_NonPositiveLimit_DoesNotTakeEmptyChunks` を足した
  - `05_transcribe_logic.html` の図 3 の説明に「録音中は普通 6 秒まで」の注記を、基準の欄に T200 / T201 で直したことを足した
  - 既存のテスト `ChunkTakeCount_SupplyIdleAtThreshold_TakesWholeBuffer` のバッファを 8 秒 → 5 秒にした（新しい既定の上限 6 秒に届かない長さで「途絶」の契機を確かめるため。テストの意図は同じ）
- 残り: T199 の B0（実際の会議で、small-q5_1 × 6 秒 × 0.8 秒・速報 OFF を試す）は利用者が行う。T199 の計測ハーネス（リポジトリ外）は `TakeNextChunk` の引数が増えたので、流用するときは `LiveChunkOptions` を渡すよう直す