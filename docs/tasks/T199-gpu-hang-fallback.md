# T199 — （不具合）GPU で文字起こし中に PC 全体が固まる。繰り返さないよう、次の起動では GPU を使わない

> **状態:** 完了 — 2026-10-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [ADR-0007](../adr/0007-two-pass-live-transcription.md)「背景」（2026-09-28 の停止）、
> [T182](./T182-provisional-pass-measurement.md)（09-29 の LiveKernelEvent 141 ×6、案 A の試走）、
> [T191](./T191-flash-attention.md)（flash attention）、T200（根本原因の切り分け）

## 1. 目的

GPU で文字起こしをしている最中に GPU のドライバーが止まり PC 全体が固まる事象を、**同じ PC で繰り返させない**。
一度起きたら次の起動で「文字起こしに GPU を使用する」を OFF にし、理由と戻し方を知らせる。

## 2. 報告された事象（利用者、2026-10-18）

- 版: `release_2026.10.18_beta`
- 速報を有効（速報用モデル base）にして録音を開始。スピーカーには YouTube の動画を流していた。
- 約 5 分でアプリの表示の更新が止まった。YouTube の動画は動き続けたが、マウスを動かすと数秒でマウスも動かなくなり、動画も止まった。
- 数分後に Ctrl+Alt+Del を押して 5〜10 分待ち、画面が切り替わったところで戻そうとしたが、
  「デバイスに問題が発生したため、PC を再起動する必要があります」の旨が出て OS が自動で再起動した。

## 3. 調査（2026-10-18）

### 3.1 アプリの中で GPU を使うのはどこか（コード）

| 処理 | 実行先 | 根拠 |
|---|---|---|
| ライブの確定パス | 「文字起こしに GPU を使用する」が ON なら GPU（この利用者の PC では Vulkan） | `TranscriptionService.LoadModel`（`TranscriptionService.cs:550` の `UseGpu = useGpu`） |
| ライブの**速報** | **常に CPU**（4 スレッド、優先度 `BelowNormal`） | `LoadProvisionalModel` が `TryCreateFactory(modelPath, useGpu: false)`（`TranscriptionService.Provisional.cs:91`）。T191 でネイティブログの「重みの載った先」が base → `CPU` であることを確かめ済み |
| ファイル文字起こし | 確定パスと同じ設定に従う | `TranscribeFileAsync`（`TranscriptionService.cs:972`） |

GPU で推論するのは確定パスのスレッド `WhisperTranscription` だけで、速報は GPU に触れない（REQ-GPU-06）。

### 3.2 症状の読み方

- 「デバイスに問題が発生したため、PC を再起動する必要があります」は Windows のバグチェック（いわゆるブルースクリーン）の画面である。
  アプリ（ユーザーモードのプロセス）が例外で落ちても OS は再起動しない。OS を止めたのはドライバーである。
- 「アプリの表示が止まる → 画面全体が止まる → 回復せずに再起動」は、GPU が応答しなくなり、Windows の GPU リセット（TDR）で回復しきれなかったときの典型的な経過で、
  2026-09-28 の停止（ADR-0007「背景」: `nvlddmkm` ID 153・LiveKernelEvent・Watchdog ダンプ）と同じ形である。
- マウスを動かすまで動画が動き続けたのは、動画の再生（デコードとオーバーレイ表示）が GPU の別の部品で動き、
  止まっていたのが計算・描画の側だったと読むと整合する（**推測**。§6 のイベントログで確かめる）。
- **未確認:** バグチェックのコード（`0x116 VIDEO_TDR_FAILURE` など）。§6 の手順で確定できる。

### 3.3 過去の記録との照合

| 日時 | 速報 | flash attention | 条件 | 結果 | 出典 |
|---|---|---|---|---|---|
| 2026-09-28 | **無し**（未実装） | 無し | アプリの録音中のライブ文字起こし、Edge で YouTube を再生してループバック録音 | 初回〜2 回目の呼び出しで PC 全体が停止、電源断 | ADR-0007「背景」 |
| 2026-09-29 04:59〜05:32 | **無し** | 無し | アプリの録音 5 本 | LiveKernelEvent 141（VIDEO_ENGINE_TIMEOUT_DETECTED）×6、`nvlddmkm` 153 ×5（回復） | T182「計測前の基準」 |
| 2026-10-05〜10-11 | あり / 無し | 一部 | **計測ハーネス**（WPF の画面無し・音声はファイルから） | 0 件 | T182 |
| 2026-10-18（今回） | あり（base） | あり | アプリの録音、YouTube を再生してループバック録音 | 約 5 分で停止 → 再起動 | 本タスク §2 |

- **速報の無い 09-28 / 09-29 にも、アプリの録音中に GPU の停止・タイムアウトが起きている。** 速報（CPU）は必要条件ではない。
- 4 回に共通するのは「確定パスが NVIDIA の GPU を Vulkan で使っている」こと。09-28 と今回はブラウザの動画再生も重なっている。
- 09-29 は 35 分の間に 6 回起きており、**一度起きた PC では続けて起きやすい**（利用者が録音をやり直すたびに起きた）。

### 3.4 結論

- 引き金は**確定パスの GPU 推論（Vulkan）**と読むのが、コードと記録の両方に最も整合する。速報の base（CPU）は直接の原因ではない。
- GPU のドライバー（`nvlddmkm`）・Whisper の Vulkan 計算部（ggml-vulkan）・同時に GPU を使うブラウザの動画再生の、どれが根本かはアプリのコードからは特定できない。T200 で利用者の実機の記録から切り分ける。
- **ドライバーが止まると、アプリ側では例外にもならず、OS ごと止まるので後始末もできない。** アプリにできるのは:
  1. GPU を使わない（「文字起こしに GPU を使用する」を OFF。**既存の設定**。確定パスとファイル文字起こしが CPU になり、遅くなる）
  2. **一度起きたら繰り返さない** — GPU で推論している間だけ印を置き、次の起動で印が残っていたら 1 を自動で行って知らせる（**本タスク**）

### 3.5 利用者の提案「base で速報が回るなら、すべて GPU で（速報 → 確定）動かしては」の評価

**採らないことを推す**（判断は利用者。§9 Q1）。

- これは [ADR-0007](../adr/0007-two-pass-live-transcription.md) の**案 A**（速報も GPU で、確定と同じ 1 本の列で直列に回す）にあたる。
  ggml の Vulkan は同じ GPU を 2 つのスレッドから同時に使うと落ちる（ggml-org/ggml#1087）ため、GPU に載せるなら確定と直列にするしかない。
- T182 の試走（10 分、Zoom なし）で**不合格**だった: 速報の遅れ p50 **8.59 秒** / p95 **24.12 秒**（合格線 1.2 / 2.0 秒）、
  確定パスの処理能力 76%（速報も足した取り分）、滞留の最大 100 秒。速報が確定の呼び出し（10 秒チャンクで 2〜4 秒）の終わりを待つ構造のため、
  flash attention で確定が約 24% 速くなっても合格線には届かない。
- **今回の事象は GPU 側の停止なので、GPU に載せる処理を増やす方向は危険を増やす。** 速報を CPU に置いているのは、確定パスの GPU の使い方を
  「速報なし」の時と同じに保つためでもある（ADR-0007 案 B の利点）。

## 4. スコープ境界

**やること**
- GPU で推論している間（ライブの録音中・ファイル文字起こし中）だけ、`%APPDATA%\AudioCaptureApp\` に印のファイルを置き、正常に終えたら消す
- 起動時に印が残っていたら、「文字起こしに GPU を使用する」を OFF にして保存し、起動直後のダイアログで理由と戻し方を知らせる（REQ-GPU-08 案）

**やらないこと（重要）**
- **確定パスの既定（GPU）を変えない** — CPU では Zoom 中に追いつかない見込み（small・CPU は 10 秒チャンクで実時間の約 1.45 倍。T191）で、速報も CPU を使う
- **flash attention の既定を変えない** — 09-28 / 09-29 は flash attention 無しで起きており、止める根拠が無い。切り分けは T200
- **速報の構成（CPU・別スレッド。ADR-0007 案 B）を変えない**
- **自動で GPU に戻さない** — 戻すのは利用者が設定ウィンドウで行う
- **根本原因の特定**（T200。利用者の実機の記録が要る）

## 5. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 印の置き場 | **`%APPDATA%\AudioCaptureApp\gpu-inference.marker`**（`settings.json` と同じフォルダ）。`settings.json` に書かないのは、設定は画面の操作のたびに丸ごと保存されるため、印の書き込みと消し込みが他の保存と入れ違いやすいから。別ファイルなら「残っているか」だけで判定できる |
| D2 | 印を置く区間 | **GPU で推論する区間だけ。** ライブは `StartSession`（確定パスのモデルが GPU に載っているとき）〜`StopSession`、ファイルは `TranscribeFileAsync` の推論の開始〜終了（GPU を使うとき）。速報は CPU なので対象外。モデルの読み込みは短いので対象外 |
| D3 | ファイル文字起こしが GPU を使うかの判定 | ライブ用のモデルを共有するなら「ライブ用のモデルが GPU に載っているか」、2 つ目のモデルを作るなら「GPU を頼んだか」（`FileTranscriptionOptions.UseGpu`。GPU が使えない環境では REQ-GPU-02 で OFF に倒れている） |
| D4 | 停止でワーカーが抜けなかったとき（`StopSession` の打ち切りのタイムアウト） | **印を消さない。** GPU の処理が続いている可能性がある。次の起動で GPU を使わない側に倒れる |
| D5 | 起動時に印が残っていたとき | 設定が ON なら **OFF にして保存し、知らせる**。OFF ならそのまま消すだけ（知らせない） |
| D6 | 知らせ方 | **起動直後に 1 度だけダイアログ**（OK のみ）。問い合わせ（はい / いいえ）にしない — 既定で PC が固まる危険を避け、戻すのは設定ウィンドウのチェック 1 つで済むため。ステータス行はモデルの読み込みの通知ですぐ上書きされるので使わない |
| D7 | 誤検知 | アプリの異常終了・停電・タスク マネージャーでの強制終了でも印は残り、区別できない（OS ごと止まったときアプリは何も書けない）。**区別しない。** 文言を「PC が固まった・強制終了した など」と幅を持たせ、戻し方を必ず書く |
| D8 | 印の書き込み・削除の失敗 | **例外を外へ出さない**（`IOException` / `UnauthorizedAccessException` を捨てる）。印は安全側の補助で、書けなくても録音と文字起こしは続ける |
| D9 | 判定の形 | `MainViewModel.ShouldFallBackToCpu(bool markerLeft, bool useGpu)`、`TranscriptionService.ShouldClearGpuMarker(bool marked, bool workerExited)`、`TranscriptionService.FileRunUsesGpu(bool sharesLiveFactory, bool liveOnGpu, bool requestedGpu)` に切り出してテストする（T191 の D4 と同じ形） |

## 6. 根本原因の確かめ方（利用者の実機）

T200 で行う。PC が再起動した後、PowerShell で次を実行すると、停止の時刻の記録が出る。

```powershell
# 直近 2 日のバグチェック・GPU ドライバー・電源断の記録
Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = (Get-Date).AddDays(-2) } |
  Where-Object { $_.ProviderName -in 'nvlddmkm', 'Display', 'Microsoft-Windows-WER-SystemErrorReporting', 'Microsoft-Windows-Kernel-Power' } |
  Format-List TimeCreated, ProviderName, Id, Message

# GPU のタイムアウトのダンプ（WATCHDOG-yyyyMMdd-HHmm.dmp の時刻で判定する。T182）とミニダンプ
Get-ChildItem C:\Windows\LiveKernelReports -Recurse -ErrorAction SilentlyContinue | Select-Object FullName, LastWriteTime
Get-ChildItem C:\Windows\Minidump -ErrorAction SilentlyContinue | Select-Object FullName, LastWriteTime
```

- `Microsoft-Windows-WER-SystemErrorReporting` の ID 1001 の本文にバグチェックのコードが出る。`0x00000116`（VIDEO_TDR_FAILURE）・
  `0x00000117` / `0x00000141`（GPU のタイムアウト）なら GPU で確定。
- 合わせて、NVIDIA ドライバーの版（`nvidia-smi` の 1 行目）と、動画を再生したブラウザのハードウェア アクセラレーションの有無を控える。
- 切り分けの候補（T200）: `settings.json` の `UseFlashAttentionOnGpu` を `false` にする／CUDA Toolkit を入れて Vulkan ではなく CUDA で動かす
  （アプリは CUDA を Vulkan より優先して読み込む。`TranscriptionService.GpuPreferredOrder`）／ブラウザで動画を流さない。

## 7. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-GPU-08（新設）
- [x] `docs/spec/02_architecture.md` — §6 永続化されるデータに印のファイル
- [x] `docs/spec/03_class_diagram.md` — `GpuInferenceMarker`、`TranscriptionService.GpuMarker`、`MainViewModel.GpuFallbackNotice`
- [x] `docs/spec/04_sequence_diagram.md` — §1 起動（印の確認）、§2 録音開始・§4 録音停止（印を置く・消す）
- [x] `docs/spec/05_transcribe_logic.html` — §8「GPU と CPU」に 1 項目、要件の欄を REQ-GPU-01〜08 に

## 8. アーキテクチャへの影響

- ADR: **不要** — 層構成・依存方向・スレッド・ライブラリを変えない。Services に小さなクラスを 1 つ足し（`SettingsService` と同じく具象クラス、インターフェース無し）、
  `TranscriptionService` へはプロパティで渡す（`AudioCaptureService.SetTranscriptionService` と同じ形）。

## 9. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/GpuInferenceMarker.cs`（新規） | 印の置き・消し・起動時の取り出し |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `GpuMarker`、確定パスのモデルが GPU に載っているかの記録、`StartSession` / `StopSession` / `TranscribeFileAsync` で印を置く・消す、判定 2 つ |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 起動時に印を確かめて GPU 使用を OFF に倒し、保存する。サービスへ印を渡す |
| `AudioCaptureApp/ViewModels/MainViewModel.Transcription.cs` | `GpuFallbackNotice`・`ShouldFallBackToCpu`・文言 |
| `AudioCaptureApp/MainWindow.xaml.cs` | 読み込み後に 1 度だけダイアログを出す |
| `AudioCaptureApp.Tests/GpuInferenceMarkerTests.cs`（新規） | 印の置き・消し・取り出し・書けないとき |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 判定 2 つ |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `ShouldFallBackToCpu` |
| `AudioCaptureApp.Tests/UiTextTests.cs` | 文言に開発情報が無いこと |
| `README.md` | 起動時に GPU が OFF になる場合の説明 |

## 10. テスト一覧

- **`TakeLeftover_WithoutMarker_ReturnsFalse`** — 印が無ければ「前回は正常」
- **`TakeLeftover_AfterSetWithoutClear_ReturnsTrueOnlyOnce`** — 置いたまま終わったら 1 度だけ `true`、取り出した後は消えている
- **`TakeLeftover_AfterClear_ReturnsFalse`** — 正常に終えて消したら `false`
- **`Set_CreatesMissingFolder`** — フォルダが無くても置ける（初回起動）
- **`SetAndClear_WhenPathIsBlocked_DoNotThrow`** — 書けない・消せないときも例外を出さない（D8）
- **`DefaultPath_IsNextToSettings`** — 置き場が `settings.json` と同じフォルダ（D1）
- **`ShouldClearGpuMarker_OnlyWhenMarkedAndWorkerExited`** — ワーカーが抜けなければ消さない（D4）
- **`FileRunUsesGpu_FollowsSharedOrOwnFactory`** — ライブ用を共有するならその実行先、2 つ目なら頼んだ実行先（D3）
- **`ShouldFallBackToCpu_OnlyWhenMarkerLeftAndGpuOn`** — 倒すのは印が残り、かつ GPU 使用が ON のときだけ（D5）
- **`GpuFallbackMessage_SaysWhatHappenedAndHowToUndo`** — 知らせる文言に「何が起きたか・何を変えたか・どう戻すか」が揃っている（D6 / D7）
- **`ViewModelText_HasNoDeveloperIdentifiers`**（対象に追加） — 知らせる文言に要件 ID などが無い（NFR-10）

> **テストで守れない範囲:** 実際に GPU のドライバーが止まったときに印が残ること（PC を止める必要がある）と、
> `StartSession` / `TranscribeFileAsync` が GPU のときに印を置くこと（Whisper のモデルと GPU が要る）。
> 前者は「印は推論の前に置き、正常に終えたときだけ消す」という作りで担保し、後者は判定を純粋関数に切り出してテストする。
> **GPU の停止そのものを防ぐものではない**（§3.4）。

## 11. 未解決の質問

1. **提案「すべて GPU で（速報 → 確定）」を採らない（§3.5）でよいか。** — 選択肢: 採らない / ADR-0007 を改訂して案 A を再計測する。*既定案: 採らない。確認をお願いします。*
2. **起動時は「自動で OFF ＋知らせる」（D6）でよいか。** — 選択肢: 自動で OFF ＋知らせる / 起動時に「GPU を使わずに起動しますか」と問い合わせる。*既定案: 自動で OFF ＋知らせる。*
3. **T200（根本原因の切り分け）を進めるか。** — §6 の記録をいただければ、flash attention・CUDA・ブラウザの動画のどれを試すかを決められる。*既定案: 記録をいただいてから T200 に着手する。*

## 12. 前提

- 停止の引き金が GPU であること（§3.4）。§6 の記録で別の原因（音声ドライバーなど）と分かった場合、本タスクの対策は「GPU を不要に止める」ことになるので、
  T200 で見直す。
- アプリが印を消すのは、正常な停止・正常な終了の経路だけであること（OS ごと止まったときは消せない）。

---

## 実行結果 (2026-10-18)

### 品質ゲート

このクラウド環境（Linux）に .NET SDK 10.0.112 を入れて G1 / G2 を実行した（T170 と同じく `-p:EnableWindowsTargeting=true`、`dotnet format` は環境変数 `EnableWindowsTargeting=true`）。
G3 は WindowsDesktop のランタイムが無いため Linux では実行できず（testhost が `Microsoft.WindowsDesktop.App` を見つけられない）、CI（windows-latest）で確かめた。

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件（Linux、`--no-incremental`）
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0、Linux）
- `dotnet test`  : **527 件成功 / 0 件失敗 / 0 件スキップ**（CI。本タスクで追加したのは 20 件 — `GpuInferenceMarkerTests` 6・`ShouldClearGpuMarker` 4・`FileRunUsesGpu` 4・`ShouldFallBackToCpu` 4・`GpuFallbackMessage` 1・`ViewModelText` 1）

CI は `build-desktop` の run 39124492925（windows-latest、workflow_dispatch、commit `cb222e6`）。同じ実行で G1 は警告 0 件 / エラー 0 件、G2 は成功。
**CI は Release 構成**で、ゲートの規定（Debug）とは構成が違う（T170 と同じ）。

### 確かめていないこと

- **実機での動作**（Windows の画面・ダイアログの表示、`%APPDATA%` への印の書き込みと削除）。この環境では WPF を動かせない。
  確かめ方: GPU で録音中にタスク マネージャーでアプリを終了 → 起動するとダイアログが出て、設定ウィンドウの「文字起こしに GPU を使用する」が OFF になっている。
  OFF のまま録音・停止して閉じ、もう一度起動するとダイアログが出ない。
- **GPU の停止そのものを防ぐものではない**（§3.4）。根本原因は T200。

### 計画からの逸脱

- テストを 2 つ足した（`DefaultPath_IsNextToSettings`・`GpuFallbackMessage_SaysWhatHappenedAndHowToUndo`）。§10 に反映済み。
- 書けないときのテストは「印の置き場がディレクトリ」ではなく「フォルダを作るべき位置にファイルがある」で作った（`TranscriptionMetricsLogTests` と同じ作り方。Windows と Linux で同じ例外の型になる）。名前を `SetAndClear_WhenPathIsBlocked_DoNotThrow` にした。
