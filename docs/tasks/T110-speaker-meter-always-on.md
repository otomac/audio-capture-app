# T110 — スピーカーのレベルメーターを録音停止中でも動作させる

> **状態:** 完了 — 2026-08-16
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

マイクのレベルメーターは録音停止中でも動作するのに対し、スピーカー（ループバック）側は
録音中しか動作しない。スピーカー側もマイクと同様、デバイスを選択している間は常時動作させる。

## 2. スコープ境界

**やること**

- ループバックキャプチャのライフサイクルを「録音の開始／停止」から
  「スピーカーデバイスの選択／解除」へ移す（マイクと同じ構造にする）
- ループバックキャプチャ開始失敗を例外送出ではなくステータス通知にする
- 無音時のピーク固着対策（§3 D2）
- ソフトミュート用無音バッファの分離（§3 D4）

**やらないこと（重要）**

- **マイク側モニタリングの構造変更。** マイクは既に常時モニタで、これが移植元になる。触らない。
- **レベルメーターの見た目・応答特性（ピークホールド、減衰カーブ等）の変更。**
  `LevelMeterControl` と `PeakToDb` はそのまま。
- **ミキシング／MP3 書き込み／文字起こしの経路変更。** 録音中の音声フローは現状を維持する。
- **スピーカーミュートの OS 連携。** マイクにある `AudioEndpointVolume` 連携をスピーカーへ広げない。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ループバックの常時化をどう実装するか | **マイクと同型の `StartLoopbackMonitor` / `StopLoopbackMonitor` を作り、`OnSelectedRenderDeviceChanged` から呼ぶ。** 既存の `SetupLoopbackCapture` / `CleanupLoopback` を置き換える |
| D2 | 再生音が無いときメーターが固着する問題 | **`LoopbackPeakLevel` の getter で無音タイムアウト（200ms）を適用し 0 を返す。** 理由は下記 |
| D3 | ループバック開始失敗（`E_HANDLE` 等）の扱い | **`StartLoopbackMonitor` は `bool` を返し、失敗時は内部を後始末して `false`。** ViewModel がステータス表示。プロパティ setter から例外を投げるとアプリが落ちる |
| D4 | `_silenceBuffer` の共有 | **マイク用／ループバック用に分離する。** 本変更で両キャプチャが常時同時稼働するようになり、既存の競合が顕在化するため（§9） |
| D5 | 録音開始時のループバックバッファ | **`ClearBuffer()` する。** 常時稼働により録音開始前の音声が溜まっており、クリアしないと録音先頭に混入する |

### D2 の根拠

WASAPI のループバックキャプチャは、レンダーエンドポイントがアイドル（何も再生していない）のとき
`IAudioCaptureClient::GetBuffer` が `AUDCLNT_S_BUFFER_EMPTY` を返し、**データパケットを生成しない**。
NAudio はこの場合 `DataAvailable` を発火しないため、`_loopbackPeakLevel` は最後の値のまま残る。

録音中だけ動かしていた従来はメーターが常に破棄されるため表面化しにくかったが、常時稼働にすると
「音楽を止めたらメーターが途中の値で止まったまま」という明確な不具合になる。
そのため、最後にデータを受け取った時刻を記録し、一定時間データが来なければ 0 とみなす。

しきい値 200ms の根拠: メーター更新は 50ms 間隔（REQ-LVL-02）、WASAPI の共有モードの
パケット周期は概ね 10ms 前後。200ms は正常な揺らぎでは到達せず、かつ人が「固まっている」と
感じる前に落ちる値として選んだ。

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - REQ-DEV-06 / 07 / 08 を新設（スピーカーの常時モニタとその失敗時挙動）
  - REQ-REC-05 を改訂（ループバックバッファもクリア）
  - REQ-MIX-07 の実装箇所を `StartLoopbackMonitor` へ
  - REQ-MUTE-08 を新設（無音バッファ分離）
  - REQ-LVL-04 を改訂（停止時リセットをやめ、常時動作を規定）
  - REQ-LVL-05 / 06 を新設（無音タイムアウト、選択解除時のリセット）
- [x] `docs/spec/03_class_diagram.md` — `AudioCaptureService` の公開メソッド増減
- [x] `docs/spec/04_sequence_diagram.md` — デバイス選択・録音開始／停止の順序変更
- [-] `docs/spec/00_overview.md` — 影響なし
- [-] `docs/spec/02_architecture.md` — 層構成・依存方向・スレッドモデルは不変

## 5. アーキテクチャへの影響

3 層構成・依存方向は不変。Service 内部のリソースライフサイクルの変更のみ。
スレッドモデルも不変（ループバックの `DataAvailable` は従来どおり NAudio のキャプチャスレッド、
UI 反映は従来どおり `_meterTimer` の UI スレッドポーリング）。

- ADR: **不要**。層の増減・依存方向変更・DI 導入・`MainViewModel` 分割・Service 抽象化・
  新規ライブラリ採用のいずれにも該当しない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AudioCaptureService.cs` | `SetupLoopbackCapture`/`CleanupLoopback` → `StartLoopbackMonitor`/`StopLoopbackMonitor`。無音タイムアウト。無音バッファ分離。`StartRecording`/`StopRecording` の調整 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `OnSelectedRenderDeviceChanged` でモニタ開始／停止。`StopRecordingAsync` の `LoopbackLevelDb = -60.0` を削除 |
| `AudioCaptureApp.Tests/AudioCaptureServiceTests.cs` | `ApplySilenceTimeout` のテストを追加 |
| `docs/spec/01_requirements.md` | §4 のとおり |
| `docs/spec/03_class_diagram.md` | 同上 |
| `docs/spec/04_sequence_diagram.md` | 同上 |

## 7. 実装手順

### グループ A — Service のライフサイクル移行

- [x] **A1** `_silenceBuffer` を `_micSilenceBuffer` / `_loopbackSilenceBuffer` に分離
- [x] **A2** `SetupLoopbackCapture` を `StartLoopbackMonitor(AudioDevice) : bool` に作り替える。
      末尾で `StartRecording()` を呼び、失敗時は後始末して `false` を返す
- [x] **A3** `CleanupLoopback` を `StopLoopbackMonitor()` に改名し、ピークリセットを含める
- [x] **A4** 無音タイムアウトを実装（`_loopbackLastDataTicks` ＋ `ApplySilenceTimeout` 純粋関数）
- [x] **A5** `StartRecording` からループバック生成を除去し、`_loopbackBuffer?.ClearBuffer()` を追加
- [x] **A6** `StopRecording` から `CleanupLoopback()` とピークリセットを除去
- [x] **A7** `Dispose` で `StopLoopbackMonitor()` を呼ぶ
- [x] **A8**（計画外・§「計画からの逸脱」）`WriterLoop` 冒頭 200ms 待機のコメントを更新

### グループ B — ViewModel

- [x] **B1** `OnSelectedRenderDeviceChanged` を追加し、モニタ開始／停止と失敗時のステータス表示
- [x] **B2** `StopRecordingAsync` から `LoopbackLevelDb = -60.0` を削除

### グループ C — テスト

- [x] **C1** `ApplySilenceTimeout` のテストを追加（§8）

### グループ Z — 検証

- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`ApplySilenceTimeout_WithinTimeout_ReturnsPeak`** — タイムアウト内ならピーク値をそのまま返す
- **`ApplySilenceTimeout_ExactlyAtTimeout_ReturnsPeak`** — 境界（経過＝しきい値）では 0 にしない
- **`ApplySilenceTimeout_BeyondTimeout_ReturnsZero`** — しきい値超過で 0 を返す（固着防止の本体）
- **`ApplySilenceTimeout_NeverReceivedData_ReturnsZero`** — 一度もデータが来ていない初期状態で 0
- **`ApplySilenceTimeout_ZeroPeakStaysZero`** — ピークが 0 のときはタイムアウトに関わらず 0

> **テストで守れない範囲:** ループバックキャプチャの起動・`DataAvailable` の発火・
> WASAPI の無音時挙動はいずれも実機の再生エンドポイントを要求するためユニットテスト不可。
> 本タスクの中核である「録音停止中もメーターが動く」こと自体は**手動確認に依存する**。
> 検証環境（サンドボックス）では全レンダーデバイスで `WasapiLoopbackCapture.StartRecording()` が
> `E_HANDLE` となり、開発機上での自動検証もできなかった。

## 9. 未解決の質問

なし。ただし §2 の「やらないこと」に反して `_silenceBuffer` の分離（D4）を本タスクに含めている。
これは既存の潜在バグだが、**本変更が顕在化させる**ため同一タスクで直すのが妥当と判断した。
（従来は「録音中かつ両方ミュート」でしか競合しなかったが、変更後は「アイドル中かつ両方ミュート」でも
両コールバックが同時に無音バッファを触る。）
別タスクに切り出すべきというレビュー判断があれば従う。

## 10. 前提

- スピーカーデバイスは既定では未選択（`SelectedRenderDevice` に既定フォールバックが無い）。
  よって「選択していなければループバックキャプチャは動かない」という現在の性質は維持される。
- 常時ループバックキャプチャを走らせても WASAPI Shared Mode のため他アプリの再生を妨げない
  （REQ-REC-03 と同じ前提）。
- 常時稼働により `_loopbackBuffer`（5 秒・`DiscardOnBufferOverflow`）が回り続けるが、
  マイク側で既に同じ構造が動いており、実用上の問題は無いと見込む。

---

## 実行結果 (2026-08-16)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **32** 件成功 / **0** 件失敗 / **0** 件スキップ
  （既存 26 件 ＋ 新規 6 件）

### 計画からの逸脱

**A8（計画外）** — `WriterLoop` 冒頭の 200ms 待機のコメントを更新した。
「ループバックの初回コールバックを待機」という記述の前提が本変更で変わったため。

待機自体は**残した**。常時モニタ化により通常は録音開始前からデータが流れているが、
「スピーカーを選択した直後に録音開始」した場合は初回コールバックが未到着のことがあるため、
待機の意味は残ると判断した。削除は挙動変更になり本タスクの目的でもないため見送った。

### 検証できなかったこと（重要）

本タスクの中核である「録音停止中もスピーカーメーターが動く」ことは**自動検証できていない**。

検証環境（サンドボックスシェル）では、5 つのレンダーデバイス**すべて**で
`WasapiLoopbackCapture.StartRecording()` が `COMException 0x80070006 (E_HANDLE)` となり、
NAudio 単体の実測プローブでもループバックキャプチャを起動できなかった。
デバイス固有ではなく環境側の制約と考えられる（同じコードが開発機の実アプリでは動作している）。

そのため以下は**手動確認が必要**:

1. 録音していない状態でスピーカーを選択 → 音楽等を再生 → メーターが振れること
2. 再生を止める → メーターが 200ms 程度で -60dB に落ちること（REQ-LVL-05 の固着防止）
3. 録音 → 停止 → 停止後もメーターが動き続けること（本タスクの目的）
4. 録音した MP3 の先頭に、録音開始前の音声が混入していないこと（REQ-REC-05）
5. スピーカーミュート ON でメーターが -60dB になり、MP3 にスピーカー音が入らないこと
