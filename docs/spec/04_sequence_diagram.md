# シーケンス図

主要なユースケースについて、実装コードから読み取れる処理の流れを Mermaid シーケンス図で示す。

## 1. アプリ起動〜初期化

```mermaid
sequenceDiagram
    actor User
    participant MW as MainWindow
    participant VM as MainViewModel
    participant SS as SettingsService
    participant ACS as AudioCaptureService
    participant TS as TranscriptionService

    User->>MW: アプリ起動
    MW->>VM: new MainViewModel()
    VM->>SS: Load()
    SS-->>VM: AppSettings
    VM->>VM: OutputFolder / TranscriptionEnabled / WhisperModelPath / UseGpuForTranscription を復元

    alt WhisperModelPath が設定済み
        VM->>VM: TryLoadWhisperModel() (非同期)
        VM->>TS: LoadModel(path, useGpu)
        TS-->>VM: (Success, GpuAvailable)
        VM->>VM: GpuAvailable 反映 / 必要ならUseGpuForTranscriptionを強制OFF
    end

    VM->>ACS: RefreshDevices()
    ACS-->>VM: CaptureDevices / RenderDevices
    VM->>VM: 前回選択デバイスを復元（マイクは無ければ既定/先頭デバイス）
    VM->>ACS: StartMicMonitor(SelectedCaptureDevice)
    ACS->>ACS: WasapiCapture 開始・AudioEndpointVolume 初期同期
    ACS-->>VM: IsMicMuted（OS側の現在値）

    opt SelectedRenderDevice != null
        VM->>ACS: StartLoopbackMonitor(SelectedRenderDevice)
        ACS->>ACS: WasapiLoopbackCapture 開始（録音と独立・常時稼働）
        ACS-->>VM: false なら StatusMessage に機能低下を表示
    end

    MW->>VM: DataContext = VM
```

## 2. 録音開始

```mermaid
sequenceDiagram
    actor User
    participant VM as MainViewModel
    participant ACS as AudioCaptureService
    participant TS as TranscriptionService
    participant Lame as LameMP3FileWriter

    User->>VM: 「録音開始」クリック (StartRecordingCommand)
    VM->>ACS: StartRecording(mic, loopback, outputFolder)
    Note over ACS: マイク・スピーカーとも常時モニタ稼働中のため<br/>ここでキャプチャの生成は行わない
    ACS->>ACS: SetupMixer() フォーマット決定・MixingSampleProvider構築
    ACS->>ACS: ファイル名生成 (yyyyMMdd_HHmmss.mp3) / フォルダ作成
    ACS->>Lame: new LameMP3FileWriter(filePath, format, STANDARD)

    opt TranscriptionEnabled かつ モデルロード済み
        ACS->>TS: RegisterSource(Mic, ...) / RegisterSource(Speaker, ...)
        ACS->>TS: StartSession(filePath, now)
        TS->>TS: WhisperTranscription スレッド起動
    end

    ACS->>ACS: _micBuffer.ClearBuffer() / _loopbackBuffer.ClearBuffer()
    ACS->>ACS: AudioMixerWriter スレッド起動 (WriterLoop)
    ACS-->>VM: 開始時刻 (DateTime)
    VM->>VM: IsRecording = true / ElapsedTime 更新開始 (_clockTimer)

    loop 20ms 周期（録音中）
        ACS->>ACS: WriterLoop: Mixer.Read() → byte変換 → Lame.Write()
    end
```

## 3. 録音中の音声取り込みと文字起こし連携

```mermaid
sequenceDiagram
    participant Mic as WasapiCapture (マイク)
    participant ACS as AudioCaptureService
    participant TS as TranscriptionService
    participant VM as MainViewModel

    Mic->>ACS: DataAvailable(buffer)
    alt IsMicMuted
        ACS->>ACS: 無音バッファを _micBuffer に追加 / MicPeakLevel = 0
    else ミュートでない
        ACS->>ACS: buffer を _micBuffer に追加
        ACS->>ACS: CalculatePeak() → MicPeakLevel
        alt 録音中 かつ TranscriptionService 接続済み
            ACS->>ACS: BytesToFloats(buffer)
            ACS->>TS: AddSamples(Mic, floats, count)
            TS->>TS: ダウンミックス + LPF + リサンプル(16kHz) → Pcm16kBuffer に蓄積
        end
    end

    Note over TS: 別スレッド (WhisperTranscription) が1秒毎にポーリング
    TS->>TS: Pcm16kBuffer が20秒分(閾値)に到達したソースを検出
    TS->>TS: SplitVoicedRegions() で有声区間に分割
    loop 有声区間ごと
        TS->>TS: WhisperProcessor.ProcessAsync(region)
        TS->>TS: セグメント毎に [時刻][ラベル]テキスト を整形して results に追加（区間の開始オフセットを加算）
        TS-->>VM: SegmentTranscribed イベント（セグメント毎、必要に応じ購読側でUI通知）
    end
    Note over TS: results は区間ループの外で宣言する。<br/>キャンセル・例外でループを抜けても次の追記は必ず通る
    TS->>TS: AppendTranscriptLines(outputPath, results)（results が空でなければ）
```

## 4. 録音停止

```mermaid
sequenceDiagram
    actor User
    participant VM as MainViewModel
    participant ACS as AudioCaptureService
    participant TS as TranscriptionService

    User->>VM: 「停止」クリック (StopRecordingCommand)
    VM->>VM: IsStopping = true / _clockTimer.Stop()
    VM->>ACS: StopRecording() ※ Task.Run 上で実行

    ACS->>ACS: _isWriting = false
    ACS->>ACS: WriterThread の終了を待機 (最大5秒)
    ACS->>TS: StopSession()
    TS->>TS: 残りバッファ(1秒以上)を処理
    TS->>TS: スレッド終了待機 (最大30秒、超過時はキャンセルして5秒待機)
    TS-->>ACS: 完了

    Note over ACS: マイク・スピーカーの常時モニタは停止しない<br/>（レベルメーターは録音停止後も動き続ける）
    ACS->>ACS: Mp3Writer.Dispose()
    alt データが一度も書き込まれなかった
        ACS->>ACS: MP3ファイルを削除 / CurrentSession = null
    else
        ACS->>ACS: CurrentSession.StoppedAt = now
    end
    ACS-->>VM: 完了

    VM->>VM: IsRecording = false / IsStopping = false
    VM->>VM: CurrentSession を参照し StatusMessage 更新（保存完了 / 文字起こしファイルの有無）
```

## 5. マイクミュートの双方向同期

```mermaid
sequenceDiagram
    actor User
    participant OS as Windows OS (AudioEndpointVolume)
    participant ACS as AudioCaptureService
    participant VM as MainViewModel

    rect rgb(235,245,255)
    Note over User,VM: ケースA: アプリ側からミュート操作
    User->>VM: ミュートボタン ON/OFF (IsMicMuted)
    VM->>ACS: IsMicMuted = value
    ACS->>ACS: _suppressMuteNotification = true
    ACS->>OS: AudioEndpointVolume.Mute = value
    OS-->>ACS: OnVolumeNotification (自分の書き込みによる通知)
    ACS->>ACS: _suppressMuteNotification が true のため無視
    ACS->>ACS: _suppressMuteNotification = false
    end

    rect rgb(255,245,235)
    Note over User,VM: ケースB: OS側（ハードウェアキー等）からミュート操作
    User->>OS: ハードウェアミュートキー押下
    OS-->>ACS: OnVolumeNotification (Muted = newValue)
    ACS->>ACS: _suppressMuteNotification == false のため処理継続
    ACS->>ACS: _isMicMuted = newValue
    ACS-->>VM: MicMuteChangedExternally(newValue) イベント (非UIスレッド)
    VM->>VM: Dispatcher.BeginInvoke で IsMicMuted を書き戻し（OSへの再書き込みは抑止）
    end
```

## 6. ファイルからの文字起こし（ドラッグ＆ドロップ含む）

```mermaid
sequenceDiagram
    actor User
    participant MW as MainWindow
    participant VM as MainViewModel
    participant TS as TranscriptionService

    alt ダイアログから選択
        User->>VM: 「音声ファイルから文字起こし」クリック
        VM->>VM: OpenFileDialog 表示 (*.wav / *.mp3)
        User->>VM: ファイル選択
    else ドラッグ＆ドロップ
        User->>MW: 音声ファイルをドロップ
        MW->>MW: TryGetSingleDroppedFile()
        MW->>VM: TranscribeDroppedFileAsync(filePath)
        VM->>VM: CanTranscribeFromFile / 拡張子チェック
    end

    VM->>VM: RunFileTranscriptionAsync(filePath)
    VM->>VM: IsTranscribingFile = true
    VM->>TS: TranscribeFileAsync(filePath, progress, token) ※Task.Run上

    TS->>TS: AudioFileReader で読み込み・チャンク毎にダウンミックス+リサンプル
    loop 閾値(20秒)到達毎
        TS->>TS: SplitVoicedRegions() で有声区間に分割
        loop 有声区間ごと
            TS->>TS: WhisperProcessor.ProcessAsync(region)
            TS->>TS: セグメント毎に [時刻][ラベル]テキスト を整形（chunkOffset + 区間先頭のオフセット を基準に加算）
            TS->>TS: StreamWriter.WriteLineAsync(line) → {入力ファイル名}.transcript.txt
        end
        TS->>TS: FlushAsync()
        TS-->>VM: progress.Report(processed, total)
        VM->>VM: FileTranscriptionStatus 更新
    end

    alt ユーザーが「中止」をクリック
        User->>VM: CancelFileTranscription()
        VM->>TS: CancellationTokenSource.Cancel()
        TS->>TS: OperationCanceledException 捕捉 → 出力ファイル削除
        TS-->>VM: throw OperationCanceledException
        VM->>VM: FileTranscriptionStatus = "中止しました"
    else 正常完了
        TS-->>VM: true
        VM->>VM: StatusMessage に出力パスを表示
    end

    VM->>VM: IsTranscribingFile = false
```

## 7. 文字起こし GPU 使用設定の切り替え

```mermaid
sequenceDiagram
    actor User
    participant VM as MainViewModel
    participant TS as TranscriptionService
    participant ACS as AudioCaptureService

    User->>VM: 「文字起こしにGPUを使用する」チェックボックス変更
    VM->>VM: OnUseGpuForTranscriptionChanged(value)
    VM->>VM: SaveSettings()
    VM->>VM: TryLoadWhisperModel() ※Task.Run上

    VM->>TS: LoadModel(modelPath, requestGpu)
    TS->>TS: DisposeProcessor() (既存モデル破棄)
    TS->>TS: RuntimeLibraryOrder = GPU優先順<br/>※実際に効くのはプロセス内で最初の読み込みのみ
    TS->>TS: LogProvider.AddLogger(...) で読み込み中だけネイティブログを購読
    TS->>TS: FromPath(modelPath, WhisperFactoryOptions{UseGpu = requestGpu})
    TS->>TS: CreateBuilder() を 1 度呼ぶ<br/>※FromPath は読み込み失敗を例外にしないため
    TS->>TS: 購読解除。ログから backends 数と重みの配置先を取得
    TS->>TS: GpuAvailable = GPU版ランタイム かつ backends >= 2<br/>実行先 = 重みの配置先が CPU 以外か
    TS-->>VM: RuntimeInfo("GPU (Vulkan)" / "CPU")
    TS-->>VM: (Success, GpuAvailable)

    alt Success
        VM->>VM: GpuAvailable 反映
        alt GPU要求だが利用不可
            VM->>VM: UseGpuForTranscription を強制 false（書き戻し抑止フラグ使用）
            VM->>VM: SaveSettings()
        end
        VM->>VM: TranscriptionStatus = "モデル読み込み完了"
    else Failure
        VM->>VM: TranscriptionStatus = "モデル読み込み失敗"
        VM->>ACS: SetTranscriptionService(null)
    end
```
