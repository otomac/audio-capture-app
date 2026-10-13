# T186 — 速報パス（速報スレッドとエンジン）を実装する

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **設計:** [T181](./T181-two-pass-live-transcription-design.md) D8〜D12 / D15、[ADR-0007](../adr/0007-two-pass-live-transcription.md)（承認済み・案 B）、実測は [T182](./T182-provisional-pass-measurement.md) の E1〜E4、E7

## 1. 目的

録音中、まだ確定していない音声を CPU の軽量モデルで句単位に文字起こしし、速報として通知する（REQ-TRX-LIVE-15〜17）。
T182 の計測ハーネス（`audio-capture-work\t182\harness` の `PhrasePlanner` / `ProvEngine`）で合格した区切り方・設定をそのまま移す。

## 2. スコープ境界

**やること**
- 速報用モデルの読み込み（`UseGpu = false`）と常駐（REQ-TRX-04）。ライブ用モデルの読み込みの後に順番に読む
- 速報スレッド `ProvisionalTranscription`（`BelowNormal`）、句の区切り（`ProvisionalPhrasePlanner`）、間隔、Whisper 設定、幻聴の除去
- `ProvisionalTranscribed` イベント（セッション ID・音源・句 ID・絶対位置・閉じたか・本文）
- 停止時に速報を先に止める（REQ-TRX-LIVE-11）
- 設定キー（REQ-CFG-12）の読み込みと `ProvisionalOptions` への正規化。計測ログへの速報の行

**やらないこと（重要）**
- **画面（字幕帯・設定ウィンドウの項目・モデル管理の追従）は T187**。本タスクの時点では `settings.json` を手で書かないと速報は動かない
- **確定パスの区切り方・出力・GPU の使い方は変えない**（ADR-0007 案 B）
- **速報はファイルに書かない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 区切りの実装 | ハーネスの `PhrasePlanner` を移す（`ProvisionalPhrasePlanner`）。位置は `long` の絶対位置（REQ-TRX-LIVE-18）。有声判定は 100ms 窓・`SilenceCut.RmsThreshold`（REQ-TRX-06 と同じ） |
| D2 | 速報用の音声 | `AddSamples` で確定パスのバッファへ積むのと同時に、速報の対象の音源だけ**別の写し**へ積む（確定パスはバッファから取り出すと消すため）。有声判定は速報スレッドで行い、キャプチャのコールバックではコピーしかしない（ハーネス §3 規則 2） |
| D3 | 写しの上限 | 今の句の先頭（句が無ければ次の句が始まりうる位置）より前は捨てる。無音が続いても溜まらない |
| D4 | 確定済みの音声 | 確定完了（REQ-TRX-LIVE-18）の位置より前には速報を出さない。句が開いていない間は探し始めをその位置まで進め、開いている句の有声がすべて確定済みならその句を閉じる（速報が遅れたときに、すぐ消される速報へ CPU を使わない） |
| D5 | processor | 呼び出しのたびに作る（`audio_ctx` が句の長さで変わるため。Whisper.net 1.9.0 は呼び出しごとにネイティブ状態を作り直すので、生成は軽い — ADR-0007「現状」） |
| D6 | Whisper 設定 | greedy・温度 0・温度フォールバック無し・単一セグメント・前文脈無し・最大トークン 80・スレッド数（既定 4）・言語は `LiveLanguage`（録音開始時に固定）・`audio_ctx` は（句の長さ＋1 秒）× 50 を 128 刻みに切り上げ（上限 1500） |
| D7 | 速報のエラー | キャンセル後の例外（`OperationCanceledException`・`WhisperProcessingException` など）は通知しない（ハーネス §3 規則 7）。それ以外の例外は**セッションにつき最初の 1 回だけ** `Error`（「速報エラー: …」）で知らせ、速報は続ける — 1 秒ごとに同じエラーでステータスを埋めないため |
| D8 | 停止 | `StopSession` の最初で速報を止める（キャンセル → `StopCancelTimeout` まで join）。速報のワーカーが抜けなければ速報用の factory を破棄しない（T117 と同じ規則。`UnloadProvisionalModel` / `Dispose` が見る） |
| D9 | 読み込みの順序 | `SettingsViewModel.TryLoadWhisperModel` の中で、ライブ用モデルの読み込みに成功した後に同じ流れで速報用モデルを読む（ネイティブログの観測は 1 つしか無いので重ねない）。同じパスが読み込み済みなら読み直さない（GPU の切り替えでライブ用を読み直しても、CPU の速報用は読み直さない） |
| D10 | 速報が動く条件 | `LiveProvisionalEnabled` が `true`・速報用モデルが読み込み済み・対象の音源が登録されている（ライブ文字起こしが ON）。どれかが欠ければスレッドを作らない |
| D12 | 句を閉じる無音の見方 | **有声の後の 0.6 秒以上の無音を、句の末尾だけでなく途中でも見る。** ハーネスの `PhrasePlanner` は呼び出しの時点の末尾の無音しか見ないため、1 秒前後の間（ま）が呼び出しの合間に収まると句が閉じず、次の発話とつながる（テストで見つけた）。閉じる位置と次の句の始まり方はハーネスと同じ。呼び出しの時点でたまたま末尾に無音がある場合の結果は変わらない |
| D11 | 速報用モデルの選択の解決 | `ProvisionalWhisperModelName` を登録一覧（REQ-CFG-08）の名前で引く。一覧に無い名前・`null` なら読み込まない（読み込み済みなら破棄する） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-04 / LIVE-11 / LIVE-14 / LIVE-15〜17 / GPU-06 / CFG-12 の実装箇所。LIVE-15 に D4、LIVE-17 に D7 を書く
- [x] `docs/spec/02_architecture.md` — §4 の `ProvisionalTranscription`、§5.2.1 の「未実装」を外し、写し（D2）を書く
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService` の速報メンバー、`ProvisionalUpdate`・`ProvisionalOptions`・`ProvisionalSources`・`ProvisionalPhrasePlanner`、`AppSettings` のキー
- [x] `docs/spec/04_sequence_diagram.md` — §3 に速報の流れ、§4 に「速報を先に止める」

## 5. アーキテクチャへの影響

- ADR: 不要 — ADR-0007（承認済み）の案 B の実装。スレッドの追加は ADR-0007 で決定済み。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `AddSamples` で写しへ積む・`ProcessChunk` で確定位置を渡す・`StopSession` で速報を先に止める・破棄 |
| `AudioCaptureApp/Services/TranscriptionService.Provisional.cs` | 新規。速報用モデル・スレッド・呼び出し・純粋関数（`AudioContextFor` / `NextProvisionalInterval` / `SanitizeProvisionalText`） |
| `AudioCaptureApp/Services/ProvisionalPhrasePlanner.cs` | 新規。句の区切り（純粋なロジック） |
| `AudioCaptureApp/Services/ProvisionalOptions.cs` | 新規。調整値の正規化と `ProvisionalUpdate` / `ProvisionalSources` |
| `AudioCaptureApp/Models/AppSettings.cs` | REQ-CFG-12 のキー |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 設定をサービスへ写す |
| `AudioCaptureApp/ViewModels/SettingsViewModel.cs` | ライブ用の後に速報用モデルを読む |
| `AudioCaptureApp.Tests/ProvisionalPhrasePlannerTests.cs` | 新規 |
| `AudioCaptureApp.Tests/ProvisionalTests.cs` | 新規（純粋関数・オプション） |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値 |

## 7. 実装手順

### グループ A — 純粋なロジック
- [x] **A1** `ProvisionalOptions`（クランプ）・`ProvisionalSources` の解釈
- [x] **A2** `ProvisionalPhrasePlanner`（ハーネスの `PhrasePlanner` を絶対位置で）
- [x] **A3** `AudioContextFor` / `NextProvisionalInterval` / `SanitizeProvisionalText`

### グループ B — サービス
- [x] **B1** 速報用モデルの読み込み・破棄
- [x] **B2** 写しへの積み込み（`AddSamples`）と確定位置（`ProcessChunk`）
- [x] **B3** 速報スレッド（間隔・呼び出し・通知・計測ログ）
- [x] **B4** 停止の順序と破棄

### グループ C — 設定と読み込み
- [x] **C1** 設定キー、`MainViewModel` での写し
- [x] **C2** `SettingsViewModel.TryLoadWhisperModel` で速報用モデルを読む

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`Plan_NoVoice_ReturnsNothing`** — 無音だけなら依頼を出さない
- **`Plan_ShortPhrase_WaitsUntilOneSecond`** — 句が 1 秒未満なら呼ばない
- **`Plan_PhraseStartsLeadBeforeFirstVoicedWindow`** — 句は最初の有声窓の 0.2 秒前から始まる
- **`Plan_TrailingSilenceOf600ms_ClosesPhrase`** — 末尾 0.6 秒の無音で閉じ、末尾の無音は渡さない
- **`Plan_LessThan300msNewVoice_DoesNotCallAgain`** — 新しい有声が 0.3 秒未満なら呼ばない
- **`Plan_ReachingMaxPhrase_ClosesAndContinuesFromCut`** — 8 秒で閉じ、続きは切った位置から始まる
- **`Plan_SilenceInsideUnplannedAudio_ClosesPhraseThere`** / **`Plan_NextPhraseAfterSilence_GetsNewId`** — 句の途中の 0.6 秒の無音で閉じる（D12）
- **`SkipBefore_IdleMovesSearchStart`** / **`SkipBefore_WholePhraseConfirmed_ClosesPhrase`** / **`SkipBefore_StraddlingPhrase_IsKept`** — 確定済みの音声の扱い（D4）
- **`KeepFrom_DuringLongSilence_Advances`** — 無音が続いても写しが溜まらない（D3）
- **`AudioContextFor_*`** — 段階への切り上げ・最小 128・上限 1500
- **`NextProvisionalInterval_*`** — duty の上限で伸びる・下限は間隔・上限 4 秒
- **`SanitizeProvisionalText_*`** — 空・定型の幻聴・繰り返しを捨て、普通の文は残す
- **`ProvisionalOptions_*`** — 範囲外・非有限の値をクランプ／既定へ倒す、音源の解釈（未知は speaker）
- **`ProvisionalTargets_*`** — どの音源に速報を出すか

> **テストで守れない範囲:** 実際の速報の遅れ・CPU 負荷・GPU との並列の安全性（T182 の実測で確認済み）、
> 速報スレッドの起動と停止の順序（モデルが要る）。実アプリでの確認は T187 の後に利用者の手元で行う（下の「実行結果」に書く）。

## 9. 未解決の質問

なし（判断が要る点は D4 / D7 / D9 で既定案を採った）。

## 10. 前提

- T185 の絶対位置・確定完了の通知が入っていること。
- ハーネスの計測は 2 音源に同じ音声を流す条件で、速報はスピーカーだけ（既定）で合格した。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 459 件成功 / 0 件失敗 / 0 件スキップ（T185 完了時 404 件 + 本タスク 55 件）
- **実モデルでの通し確認**（スクラッチの検証プログラム。`TranscriptionService` の公開 API だけを使い、確定用 small・GPU（Vulkan）、速報用 base・CPU 4 スレッド。T182 の `ma02` を実時間で `AddSamples`。利用者の `settings.json` には触れていない）:

  | 条件 | 速報 | 速報の遅れ（通知 − 句の終端） | 確定完了の通知 | 停止 | エラー |
  |---|---|---|---|---|---|
  | スピーカーだけ（既定）・120 秒 | 108 件・17 句 | p50 0.76 / p95 1.54 / 最大 1.70 秒 | 12 件・終端は単調増加 | 滞留 10 秒を 4.6 秒で吐き切り。停止開始の 0.5 秒後以降の速報 0 件 | 0 件 |
  | 両方（`both`）・45 秒 | 63 件・12 句 | p50 0.74 / p95 1.90 / 最大 2.99 秒 | 10 件・単調増加 | 滞留 20 秒を 8.0 秒で吐き切り。停止後の速報 0 件 | 0 件 |

  - 速報用モデルの読み込みは 119〜306ms、同じパスの 2 回目は 0ms（D9）。
  - 計測ログ: 1 行目が列名、確定 12 行・速報 110 行、本文なし。確認後に検証で作ったログは消した。
  - 通知の時点で確定済みの範囲に収まる速報（すぐ消されるもの）は 0 件。
  - 遅れはここでは「句の終端の音声が届いてから通知まで」で、T182 の指標（有声窓ごとに最初に表示されるまで）とは定義が違う。目安として見る。
- 計画からの逸脱: **D12 を足した**（句の途中の 0.6 秒の無音でも閉じる）。テスト `Plan_NextPhraseAfterSilence_GetsNewId` で、ハーネスの規則は呼び出しの合間に収まった間（ま）を見逃すと分かったため。REQ-TRX-LIVE-16 にも書いた。
- 未確認: Zoom 会議中の性能（NFR-11）は T182 で測った方式そのままのため測り直していない。画面での見え方は T187。
