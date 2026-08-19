# ソフトウェアアーキテクチャ

## 1. 技術スタック

| 項目 | 内容 |
|---|---|
| 言語 | C# 14 / .NET 10 (`net10.0-windows`) |
| UI | WPF + CommunityToolkit.Mvvm 8.3.2（`[ObservableProperty]` / `[RelayCommand]` ソースジェネレータ） |
| 録音 | NAudio 2.2.1 + NAudio.Wasapi 2.2.1（WASAPI Shared Mode） |
| MP3 エンコード | NAudio.Lame 2.1.0（`LameMP3FileWriter`） |
| 文字起こし | Whisper.net 1.9.0 + Whisper.net.Runtime / Runtime.Cuda / Runtime.Vulkan（GGML モデル） |
| 設定永続化 | System.Text.Json（`settings.json`） |
| テスト | xUnit（`AudioCaptureApp.Tests`） |

## 2. レイヤー構成

[CLAUDE.md](../../CLAUDE.md) の方針に従い、Models / ViewModels / Services の 3 層構成を採用する。ViewModel は `MainViewModel.cs` 1 ファイルに集約している（シンプル優先）。

```mermaid
graph TB
    subgraph View["View層"]
        MW["MainWindow.xaml / .xaml.cs"]
        LMC["LevelMeterControl"]
    end

    subgraph ViewModel["ViewModel層"]
        MVM["MainViewModel"]
    end

    subgraph Service["Service層"]
        ACS["AudioCaptureService"]
        TS["TranscriptionService"]
        SS["SettingsService"]
    end

    subgraph Model["Model層"]
        AD["AudioDevice"]
        RS["RecordingSession"]
        AS["AppSettings"]
    end

    subgraph External["外部ライブラリ / OS"]
        NAudio["NAudio (WASAPI / MMDevice)"]
        Lame["NAudio.Lame (LameMP3FileWriter)"]
        Whisper["Whisper.net (WhisperFactory / WhisperProcessor)"]
        FS["ファイルシステム (settings.json, *.mp3, *.txt)"]
    end

    MW -->|"DataContext"| MVM
    MW --> LMC
    LMC -->|"Level (dB) バインド"| MVM

    MVM --> ACS
    MVM --> TS
    MVM --> SS
    MVM --> AD

    ACS --> RS
    ACS --> AD
    ACS -->|"AddSamples / RegisterSource"| TS
    ACS --> NAudio
    ACS --> Lame

    TS --> Whisper
    TS --> FS

    SS --> AS
    SS --> FS
```

### 各層の責務

- **View層**（`MainWindow.xaml(.cs)`, `Controls/LevelMeterControl`）
  UI 表示とユーザー操作の受け付け。ドラッグ＆ドロップのイベントハンドリングと、`MainViewModel` へのバインディングのみを持ち、業務ロジックは持たない。
- **ViewModel層**（`ViewModels/MainViewModel`）
  UI 状態（録音中／設定値／進捗等）の保持、コマンド（`[RelayCommand]`）によるユーザー操作のハンドリング、Service 層の呼び出しオーケストレーション、`DispatcherTimer` による定期更新（メーター 50ms／経過時間 1s）を担う。
- **Service層**（`Services/AudioCaptureService`, `TranscriptionService`, `SettingsService`）
  NAudio・Whisper.net・ファイル I/O など外部リソースを直接操作する。ViewModel から独立してテスト可能な static ヘルパー（`BytesToFloats` / `CalculatePeak` / `SplitVoicedRegions` など）を公開し、`AudioCaptureApp.Tests` から `InternalsVisibleTo` 経由で検証する。
- **Model層**（`Models/AudioDevice`, `RecordingSession`, `AppSettings`）
  可変・不変データを保持する POCO。ロジックを持たない。

## 3. コンポーネント間の主要な依存関係

- `MainWindow` は `MainViewModel` を直接 `new` して `DataContext` に設定する（DI コンテナは使用しない、シンプル優先の方針）。
- `MainViewModel` は `AudioCaptureService` / `TranscriptionService` / `SettingsService` をフィールドとして保持し、直接インスタンス化する。
- `AudioCaptureService` はライブ文字起こしのために `TranscriptionService` への参照を `SetTranscriptionService` で受け取る（null 許容、疎結合）。録音中のみ音声サンプルを `AddSamples` で渡す。
- `TranscriptionService` は `AudioCaptureService` を一切参照しない（一方向依存）。

## 4. スレッドモデル

本アプリは UI スレッドに加え、複数のバックグラウンドスレッド／コールバックスレッドが協調して動作する。

| スレッド | 起点 | 役割 |
|---|---|---|
| UI スレッド | WPF Dispatcher | ユーザー操作、データバインディング更新、`DispatcherTimer`（メーター更新・経過時間更新） |
| マイクキャプチャコールバック | `WasapiCapture.DataAvailable`（NAudio 内部スレッド） | マイク音声をバッファへ追加、ピークレベル算出、（録音中は）文字起こしバッファへ追加 |
| ループバックキャプチャコールバック | `WasapiLoopbackCapture.DataAvailable`（NAudio 内部スレッド） | スピーカー音声をバッファへ追加、ピークレベル算出、（録音中は）文字起こしバッファへ追加 |
| `AudioMixerWriter` | `AudioCaptureService.WriterLoop`（録音中のみ起動する専用 `Thread`） | 20ms 周期でミキサーから読み出し、MP3 へストリーミング書き込み |
| `WhisperTranscription` | `TranscriptionService.TranscriptionLoop`（ライブ文字起こしセッション中のみ起動する専用 `Thread`） | 1 秒周期で各音声ソースのバッファを確認し、閾値到達分を Whisper で処理 |
| ハードウェアミュート通知 | `AudioEndpointVolume.OnVolumeNotification`（OS コールバック） | OS 側のミュート変更をアプリへ通知（非 UI スレッド） |
| `Task.Run` ワーカー | `MainViewModel.StopRecordingAsync` / `TryLoadWhisperModel` / `RunFileTranscriptionAsync` | 録音停止・モデルロード・ファイル文字起こしなど時間のかかる処理を UI スレッドから退避 |

UI スレッド以外からプロパティを更新する箇所（`MicMuteChangedExternally`、`TranscriptionService.Error`/`RuntimeInfo`、`AudioCaptureService.RecordingError`）はすべて `Application.Current.Dispatcher.BeginInvoke` を介して UI スレッドに戻す（[CLAUDE.md](../../CLAUDE.md) の開発ルールに準拠）。

## 5. データフロー概要

### 5.1 録音・ミキシング・保存

```mermaid
flowchart LR
    Mic["マイク (WasapiCapture)"] -->|DataAvailable| MicBuf["_micBuffer\n(BufferedWaveProvider)"]
    Speaker["スピーカー (WasapiLoopbackCapture)"] -->|DataAvailable| LoopBuf["_loopbackBuffer\n(BufferedWaveProvider)"]
    MicBuf --> Mixer["MixingSampleProvider\n(フォーマット変換込み)"]
    LoopBuf --> Mixer
    Mixer -->|20ms周期 Read| Writer["WriterLoop スレッド"]
    Writer --> Lame["LameMP3FileWriter"]
    Lame --> Mp3["*.mp3"]
```

### 5.2 文字起こし（ライブ）

```mermaid
flowchart LR
    MicBuf2["マイクコールバック"] -->|"AddSamples(Mic)"| SrcMic["SourceState (Mic)\nダウンミックス+LPF+リサンプル"]
    LoopBuf2["スピーカーコールバック"] -->|"AddSamples(Speaker)"| SrcSpk["SourceState (Speaker)"]
    SrcMic -->|20秒分たまったら| Whisper1["WhisperProcessor"]
    SrcSpk -->|20秒分たまったら| Whisper2["WhisperProcessor"]
    Whisper1 --> Txt["*.txt (同名, 追記)"]
    Whisper2 --> Txt
```

## 6. 永続化されるデータ

| データ | 保存先 | 備考 |
|---|---|---|
| アプリ設定 | `%APPDATA%\AudioCaptureApp\settings.json` | `SettingsService` が JSON で読み書き |
| 録音ファイル | `<OutputFolder>\yyyyMMdd_HHmmss.mp3`（既定 `%USERPROFILE%\Documents\AudioCapture`） | `AudioCaptureService.StartRecording` |
| ライブ文字起こし結果 | 録音ファイルと同名の `.txt` | `TranscriptionService.StartSession` |
| ファイル文字起こし結果 | `{入力ファイル名}.transcript.txt` | `TranscriptionService.BuildTranscriptPath` |
| Whisper モデル | 既定 `%APPDATA%\AudioCaptureApp\models\ggml-small.bin`（ユーザー変更可） | `AppSettings.WhisperModelPath` |

## 7. エラーハンドリング方針

- Service 層は例外を握りつぶさず、原則として `InvalidOperationException` として再送出するか、`Error`/`RecordingError` イベントで非同期に通知する。
- ViewModel は Service からの例外・イベントを捕捉し、`StatusMessage` にユーザー向けメッセージとして表示する（例外の握り潰しではなく UI へのフィードバックに変換）。
- デバイス固有の機能制限（`AudioEndpointVolume` 非対応等）は例外を catch した上で機能を縮退させ、処理継続を優先する（ソフトミュートへのフォールバック等）。
