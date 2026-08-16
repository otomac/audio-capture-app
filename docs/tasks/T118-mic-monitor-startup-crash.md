# T118 — マイクのモニタリング開始失敗でアプリが起動できない

> **状態:** 完了 — 2026-08-16
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

マイクの常時モニタリング開始に失敗すると、アプリが起動できずクラッシュする。
T110 でスピーカー側にのみ入れた REQ-DEV-08 の対策をマイク側にも適用する。

## 2. 調査結果

Windows アプリケーションログ（2026-08-16 13:33:46）に記録されていた実スタック。

```
System.Windows.Markup.XamlParseException:
  型 'AudioCaptureApp.MainWindow' のコンストラクターの呼び出しで例外がスローされました。
 ---> System.Runtime.InteropServices.COMException (0x80070006): ハンドルが無効です。 (E_HANDLE)
   at NAudio.CoreAudioApi.AudioClient.Initialize(...)
   at NAudio.CoreAudioApi.WasapiCapture.InitializeCaptureDevice()
   at NAudio.CoreAudioApi.WasapiCapture.StartRecording()
   at AudioCaptureApp.Services.AudioCaptureService.StartMicMonitor(AudioDevice device)
   at AudioCaptureApp.ViewModels.MainViewModel.OnSelectedCaptureDeviceChanged(AudioDevice value)
   at AudioCaptureApp.ViewModels.MainViewModel.set_SelectedCaptureDevice(AudioDevice value)
   at AudioCaptureApp.ViewModels.MainViewModel..ctor()
   at AudioCaptureApp.MainWindow..ctor()
```

`StartMicMonitor` は `SelectedCaptureDevice` の **setter** から呼ばれ、その setter は
`MainViewModel` のコンストラクターからも実行される。したがって例外が漏れると
`MainWindow` の生成が失敗し、**アプリが起動できない**。

T110 では同じ問題をスピーカー側で予見して `StartLoopbackMonitor` を
「例外を投げず `false` を返す」形にしたが、**マイク側は「触らない」とスコープ外にしていた**。
そのスコープ判断の漏れが本件。

## 3. 修正

- `StartMicMonitor` の戻り値を `void` → `bool` に変更し、本体を `SetupMicCapture` へ切り出して
  全体を try/catch で包む。失敗時は `StopMicMonitor()` で後始末して `false` を返す。
- `MainViewModel.OnSelectedCaptureDeviceChanged` は `false` のとき
  ステータスメッセージで通知し、**OS 側ミュート状態の反映は行わない**（取得できないため）。

`AudioEndpointVolume` を取得できないデバイス向けの既存の内側 catch はそのまま残している
（こちらは「ソフトミュートのみで継続」という別の機能低下）。

### スコープ境界

**やらないこと**

- **`RefreshDevices` / `EnumerateDevices` の例外対策。** `device.FriendlyName` の取得等でも
  COM 例外が出る可能性はあるが、**実際に発生した記録が無い**ため今回は触らない。
  発生を観測したら別タスクで起票する。
- **スピーカー側の変更。** T110 で対策済み。

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - REQ-DEV-08 を改訂（スピーカー限定 → マイク／スピーカー両方。起動不能になる理由を明記）
  - REQ-DEV-09 を新設（失敗時は OS ミュート状態を反映しない）
- [x] `docs/spec/03_class_diagram.md` — `StartMicMonitor` の戻り値
- [-] その他の章 — 影響なし

## 5. アーキテクチャへの影響

なし。ADR: **不要**。

## 6. テスト

**新規ユニットテストは追加しない。** 本修正は WASAPI デバイスの初期化失敗という
実機依存の経路であり、純粋関数として切り出せる判断ロジックが無い。
代わりに実測プローブで検証した（下記）。

---

## 実行結果 (2026-08-16)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **53** 件成功 / **0** 件失敗 / **0** 件スキップ

### 実測プローブ（WPF と同じ STA スレッドで `AudioCaptureService` を直接叩く）

```
マイクデバイス 2 件
  StartMicMonitor -> true (成功)   スピーカーフォン (Brio 300)
  StartMicMonitor -> true (成功)   マイク (Realtek(R) Audio)

スピーカーデバイス 5 件（T110 で対策済み・比較用）
  StartLoopbackMonitor -> true (成功)   ... 5 件すべて成功

失敗経路の検証（存在しないデバイス ID）
  StartMicMonitor      -> false (例外を投げずに失敗を通知)
  StartLoopbackMonitor -> false (例外を投げずに失敗を通知)

失敗後に正常デバイスで再開できるか
  StartMicMonitor(スピーカーフォン (Brio 300)) -> True
```

**例外は 1 件も漏れなかった。** 失敗後に正常デバイスで再開できることから、
`StopMicMonitor()` による後始末も効いている。

> **注意:** 検証時点では `E_HANDLE` が再現しなかった（以前は全デバイスで発生していたが、
> 音声セッションの状態に依存する）。そのため失敗経路は「存在しないデバイス ID」で
> 代替検証している。**実際の `E_HANDLE` 発生時に `false` が返ることは未観測**であり、
> try/catch の構造上そうなるという根拠に留まる。
