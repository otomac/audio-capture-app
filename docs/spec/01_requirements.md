# 要件リスト

ソースコード（`AudioCaptureApp/Models`, `Services`, `ViewModels`, `MainWindow.xaml(.cs)`, `Controls`）から抽出した機能要件・非機能要件を記載する。
各要件には実装箇所を付記する。

## 1. デバイス管理

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-DEV-01 | 起動時および「更新」操作時に、有効な録音（Capture）デバイスと再生（Render）デバイスの一覧を取得できる | `AudioCaptureService.RefreshDevices`, `EnumerateDevices` |
| REQ-DEV-02 | デバイス一覧取得時、それぞれの既定デバイス（マイクは Communications ロール、スピーカーは Multimedia ロール）を判定し `IsDefault` としてマークする | `AudioCaptureService.EnumerateDevices` |
| REQ-DEV-03 | マイクデバイスを選択すると、録音の有無に関わらず常時モニタリング（キャプチャ）を開始する | `AudioCaptureService.StartMicMonitor`, `MainViewModel.OnSelectedCaptureDeviceChanged` |
| REQ-DEV-04 | マイクデバイスの選択を解除すると常時モニタリングを停止する | `AudioCaptureService.StopMicMonitor` |
| REQ-DEV-05 | 前回選択したマイク／スピーカーデバイス ID を設定に保存し、次回起動時に一覧内に存在すれば自動選択する。見つからない場合、マイクは既定デバイスまたは先頭デバイスにフォールバックする | `MainViewModel` コンストラクタ, `AppSettings.LastSelectedDeviceId`, `LastSelectedLoopbackDeviceId` |

## 2. 録音制御

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-REC-01 | マイクまたはスピーカーの少なくとも一方が選択されていれば録音を開始できる。両方とも未選択の場合は例外を送出する | `AudioCaptureService.StartRecording` |
| REQ-REC-02 | 録音中に再度開始しようとした場合はエラーとする（多重録音防止） | `AudioCaptureService.StartRecording` |
| REQ-REC-03 | 録音は WASAPI Shared Mode で行い、他アプリと同一デバイスを排他せずに共有する | `WasapiCapture { ShareMode = AudioClientShareMode.Shared }` |
| REQ-REC-04 | 録音開始時にファイル名 `yyyyMMdd_HHmmss.mp3` を自動生成し、保存先フォルダに出力する。フォルダが存在しない場合は自動作成する | `AudioCaptureService.StartRecording` |
| REQ-REC-05 | 録音開始時、マイクの常時モニタバッファをクリアし、録音開始時点以降の音声のみを含める | `AudioCaptureService.StartRecording` (`_micBuffer?.ClearBuffer()`) |
| REQ-REC-06 | 録音停止時、実際に音声データが一度も書き込まれなかった場合は生成した MP3 ファイルを削除する | `AudioCaptureService.StopRecording` (`_hasWrittenData`) |
| REQ-REC-07 | 録音の開始・停止に伴い UI に録音状態（停止中／録音中／停止処理中）と経過時間（1 秒更新）を表示する | `MainViewModel.IsRecording/IsStopping/ElapsedTime`, `_clockTimer` |
| REQ-REC-08 | 録音停止操作はバックグラウンドスレッドで実行し、UI スレッドをブロックしない | `MainViewModel.StopRecordingAsync` (`Task.Run`) |
| REQ-REC-09 | 録音・録音停止処理中・ファイル文字起こし中は、デバイス選択／更新／保存先変更／モデル選択などの操作を無効化する（排他制御） | `MainViewModel` 各 `CanExecute` |
| REQ-REC-10 | 録音処理中に例外が発生した場合、録音を安全に停止しエラーメッセージを UI に表示する | `AudioCaptureService.RecordingError` イベント, `MainViewModel.OnRecordingError` |

## 3. 音声ミキシング・エンコード

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-MIX-01 | マイク音声とループバック（スピーカー）音声を 1 つの `MixingSampleProvider` で合成する。どちらか一方のみの場合は単独ソースをそのまま使用する | `AudioCaptureService.SetupMixer` |
| REQ-MIX-02 | 出力フォーマットはマイク／ループバックのうち高い方のサンプルレート・チャンネル数を採用した IEEE Float フォーマットとする | `AudioCaptureService.SetupMixer` |
| REQ-MIX-03 | チャンネル数・サンプルレートが出力フォーマットと異なるソースは、モノラル→ステレオ変換およびリサンプリング（`WdlResamplingSampleProvider`）で合わせる | `AudioCaptureService.MatchFormat` |
| REQ-MIX-04 | ミキシング結果を `LameMP3FileWriter` で MP3 エンコードしながら専用スレッドで 20ms 周期でファイルに書き込む | `AudioCaptureService.WriterLoop` |
| REQ-MIX-05 | 書き込みタイミングは `Stopwatch` による高精度タイマーで管理し、遅延が 60ms 以上蓄積した場合はタイミングをリセットしてスキップする | `AudioCaptureService.WriterLoop` |
| REQ-MIX-06 | 録音停止時、バッファに残っているデータをすべて読み切ってからファイルをクローズする（データ欠落防止） | `AudioCaptureService.WriterLoop` 終端処理 |
| REQ-MIX-07 | データが到着していないソースは自動的に無音（ゼロ埋め）として扱われる（`BufferedWaveProvider.ReadFully = true`） | `AudioCaptureService.SetupLoopbackCapture`, `StartMicMonitor` |

## 4. ミュート制御

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-MUTE-01 | マイクミュート ON 時、キャプチャした音声の代わりに無音データをバッファへ書き込む（ソフトミュート） | `AudioCaptureService` (`_isMicMuted` 分岐) |
| REQ-MUTE-02 | スピーカー（ループバック）ミュート ON 時も同様にソフトミュートを適用し、マイク側には影響しない | `AudioCaptureService` (`_isSpeakerMuted` 分岐) |
| REQ-MUTE-03 | マイクデバイスが `AudioEndpointVolume` インターフェースをサポートする場合、アプリのミュート操作を OS 側のハードウェアミュートにも反映する | `AudioCaptureService.TryApplyMuteToEndpoint` |
| REQ-MUTE-04 | OS 側（ハードウェアキー等）でマイクミュートが変更された場合、`OnVolumeNotification` を受けてアプリ側の状態と UI を同期する | `AudioCaptureService.OnMicVolumeNotification`, `MicMuteChangedExternally` イベント, `MainViewModel.OnMicMuteChangedExternally` |
| REQ-MUTE-05 | アプリ発の OS への書き込みによって発生する通知の折り返し（無限ループ）を、書き込み中フラグで抑止する | `_suppressMuteNotification` |
| REQ-MUTE-06 | `AudioEndpointVolume` が取得できないデバイスでは、ソフトミュートのみで動作を継続する（例外を握りつぶし機能低下として扱う） | `AudioCaptureService.StartMicMonitor` catch 節 |
| REQ-MUTE-07 | マイクデバイス切替時、OS 側の現在のミュート状態を ViewModel に反映する（この際は OS への書き戻しを行わない） | `MainViewModel.OnSelectedCaptureDeviceChanged` |

## 5. 音声レベルメーター

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-LVL-01 | マイク／スピーカーそれぞれの音声バッファからピークレベル（0.0〜1.0）を算出する（IEEE Float 32bit / PCM 16bit に対応） | `AudioCaptureService.CalculatePeak` |
| REQ-LVL-02 | ピークレベルを 50ms 間隔で dB 値（-60〜+3dB にクランプ）に変換して UI に反映する | `MainViewModel._meterTimer`, `PeakToDb` |
| REQ-LVL-03 | ミュート中はピークレベルを 0（＝-60dB 相当）として扱う | `AudioCaptureService` (ミュート時 `_micPeakLevel = 0f` 等) |
| REQ-LVL-04 | 録音停止時、スピーカー側のレベル表示を即座に -60dB にリセットする | `MainViewModel.StopRecordingAsync` |

## 6. 設定の永続化

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-CFG-01 | 保存先フォルダ・前回選択デバイス・文字起こし設定（有効化／モデルパス／GPU 使用有無）を JSON ファイルとして `%APPDATA%\AudioCaptureApp\settings.json` に永続化する | `SettingsService`, `AppSettings` |
| REQ-CFG-02 | 設定ファイルが存在しない、または読み込みに失敗した場合は既定値の `AppSettings` にフォールバックする | `SettingsService.Load` |
| REQ-CFG-03 | 保存先フォルダの既定値は `%USERPROFILE%\Documents\AudioCapture` とする | `AppSettings.OutputFolder` |
| REQ-CFG-04 | Whisper モデルパスの既定値は `%APPDATA%\AudioCaptureApp\models\ggml-small.bin` とする | `AppSettings.WhisperModelPath` |
| REQ-CFG-05 | 保存先フォルダ変更・録音開始・文字起こし設定変更などの操作直後に設定を都度保存する | `MainViewModel.SaveSettings` の各呼び出し箇所 |

## 7. 文字起こし（共通基盤）

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-01 | Whisper GGML モデルファイルを指定して読み込める。存在しないパスの場合は失敗を返す | `TranscriptionService.LoadModel` |
| REQ-TRX-02 | モデル読み込み時、GPU 優先順（CUDA > Vulkan > CoreML > OpenVino > CPU）で一度読み込みを試み、実際に GPU が利用可能かを判定する | `TranscriptionService.LoadModel`, `GpuPreferredOrder` |
| REQ-TRX-03 | GPU が利用可能でもユーザー設定が CPU 使用の場合は、CPU 限定順で読み込み直す | `TranscriptionService.LoadModel` |
| REQ-TRX-04 | モデルパスが設定されていれば、ライブ文字起こしの ON/OFF に関わらず常にモデルをロードする（ファイル文字起こしはライブ設定と独立して動作するため） | `MainViewModel` コンストラクタ, `TryLoadWhisperModel` |
| REQ-TRX-05 | 入力音声はダウンミックス（ステレオ→モノラル）・1 次 IIR ローパスフィルタ・線形リサンプリングにより 16kHz モノラルへ変換してから Whisper に渡す | `TranscriptionService.DownmixResampleAppend` |
| REQ-TRX-06 | RMS が -40dB 相当（0.01）未満のチャンクは無音とみなし Whisper に渡さない（ハルシネーション防止） | `TranscriptionService.IsSilent` |
| REQ-TRX-07 | 文字起こし結果は `[開始時刻 - 終了時刻] [ラベル] テキスト` 形式の行としてテキストファイルに追記する | `TranscriptionService.ProcessChunk`, `ProcessFileChunkAsync` |

## 8. ライブ文字起こし（録音中）

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-LIVE-01 | 「録音中にライブ文字起こしする」が ON かつモデルロード済みの場合のみ、録音サービスに文字起こしサービスを接続する | `MainViewModel.OnTranscriptionEnabledChanged` |
| REQ-TRX-LIVE-02 | 録音開始時、マイク・スピーカーそれぞれを音声ソースとして登録し（`マイク`/`スピーカー` ラベル）、出力パスは録音 MP3 と同名の `.txt` とする | `AudioCaptureService.StartRecording`, `TranscriptionService.RegisterSource/StartSession` |
| REQ-TRX-LIVE-03 | 録音中、マイク／スピーカーの音声チャンクをそれぞれ非同期にバッファへ蓄積する | `AudioCaptureService` DataAvailable ハンドラ, `TranscriptionService.AddSamples` |
| REQ-TRX-LIVE-04 | バックグラウンドスレッドが 1 秒周期でポーリングし、各ソースにつき 20 秒分（16kHz 換算）バッファが溜まったらまとめて Whisper に投入する | `TranscriptionService.TranscriptionLoop` |
| REQ-TRX-LIVE-05 | 録音停止時、残りバッファ（1 秒以上ある場合）を処理してからセッションを終了する | `TranscriptionService.StopSession`, `TranscriptionLoop` 終端処理 |
| REQ-TRX-LIVE-06 | セッション終了処理は最大 30 秒待機し、タイムアウトした場合はキャンセルして強制終了する | `TranscriptionService.StopSession` |

## 9. ファイルからの文字起こし

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-FILE-01 | モデルロード済みの場合、ファイル選択ダイアログ（`*.wav;*.mp3`）から音声ファイルを選び文字起こしできる | `MainViewModel.TranscribeFromFileAsync` |
| REQ-TRX-FILE-02 | 対応拡張子（.wav / .mp3）のファイルをウィンドウ上の文字起こしグループへドラッグ＆ドロップして文字起こしを開始できる | `MainWindow.xaml.cs` (`Drop` イベント), `MainViewModel.TranscribeDroppedFileAsync` |
| REQ-TRX-FILE-03 | 非対応の拡張子がドロップされた場合はエラーメッセージを表示し処理しない | `MainViewModel.TranscribeDroppedFileAsync`, `IsSupportedAudioExtension` |
| REQ-TRX-FILE-04 | ドラッグオーバー中、ドロップ可能かどうかに応じてオーバーレイ表示を切り替える | `MainWindow.xaml.cs` (`DragOver`/`DragLeave`) |
| REQ-TRX-FILE-05 | 出力ファイルは `{入力ファイル名}.transcript.txt`（録音時生成の `.txt` と名前衝突しない命名）として同フォルダに保存する | `TranscriptionService.BuildTranscriptPath` |
| REQ-TRX-FILE-06 | 処理中は進捗（処理済み時間／総時間）を UI に表示する | `MainViewModel.RunFileTranscriptionAsync`, `IProgress<(TimeSpan,TimeSpan)>` |
| REQ-TRX-FILE-07 | 処理中に「中止」操作でキャンセルできる。キャンセル時は生成中の出力ファイルを削除し、部分結果を残さない | `MainViewModel.CancelFileTranscription`, `TranscriptionService.TranscribeFileAsync` (catch `OperationCanceledException`) |
| REQ-TRX-FILE-08 | ファイル文字起こし処理は UI スレッドをブロックしないようバックグラウンドスレッドで実行する | `MainViewModel.RunFileTranscriptionAsync` (`Task.Run`) |

## 10. GPU 使用切り替え

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-GPU-01 | 「文字起こしに GPU を使用する」設定を保持し、既定値は ON（true）とする | `AppSettings.UseGpuForTranscription` |
| REQ-GPU-02 | モデル読み込みの結果、実際に GPU が利用不可と判明した場合は設定を強制的に OFF にし、UI 上でも操作不可（無効化）にする | `MainViewModel.TryLoadWhisperModel`, `GpuAvailable`, `CanToggleGpu` |
| REQ-GPU-03 | GPU 使用設定を切り替えると、現在のモデルを破棄して新しい設定で再読み込みする | `MainViewModel.OnUseGpuForTranscriptionChanged` → `TryLoadWhisperModel` |
| REQ-GPU-04 | 録音中・録音停止処理中・ファイル文字起こし中は GPU 使用設定の切り替えを禁止する | `MainViewModel.CanToggleGpu` (`IsNotBusy && GpuAvailable`) |
| REQ-GPU-05 | ロードされたランタイム種別（CPU/CUDA/Vulkan 等）をステータスメッセージとして通知する | `TranscriptionService.RuntimeInfo` イベント |

## 非機能要件

| ID | 要件 | 補足 |
|---|---|---|
| NFR-01 | UI スレッド以外から UI プロパティを更新する場合は必ず `Dispatcher` 経由で行う | [CLAUDE.md](../../CLAUDE.md) の開発ルールに準拠。`MainViewModel` 内の各イベントハンドラで徹底 |
| NFR-02 | NAudio を直接利用し、独自の録音抽象化レイヤーを設けない | [CLAUDE.md](../../CLAUDE.md) のアーキテクチャ方針 |
| NFR-03 | 音声処理（キャプチャコールバック、MP3 書き込み、文字起こし）はいずれも専用スレッド／バックグラウンドタスクで実行し、UI の応答性を阻害しない | `AudioMixerWriter` スレッド、`WhisperTranscription` スレッド、`Task.Run` |
| NFR-04 | `AudioCaptureService` / `TranscriptionService` は `IDisposable` を実装し、確保したネイティブリソース（デバイスハンドル・エンコーダ・Whisper プロセッサ）を確実に解放する | 各サービスの `Dispose` |
| NFR-05 | デバイス権限不足やハードウェア非対応など回復可能なエラーは機能を縮退（ソフトミュートのみ等）させて処理を継続し、アプリを落とさない | `AudioCaptureService` の try/catch 群 |
| NFR-06 | 長時間録音時もメモリを蓄積しないよう、MP3 エンコード・書き込みはストリーミング方式とする | `LameMP3FileWriter` へのチャンク単位書き込み |
