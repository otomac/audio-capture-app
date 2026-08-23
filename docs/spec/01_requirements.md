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
| REQ-DEV-06 | スピーカーデバイスを選択すると、録音の有無に関わらず常時モニタリング（ループバックキャプチャ）を開始する | `AudioCaptureService.StartLoopbackMonitor`, `MainViewModel.OnSelectedRenderDeviceChanged` |
| REQ-DEV-07 | スピーカーデバイスの選択を解除すると常時モニタリングを停止する | `AudioCaptureService.StopLoopbackMonitor` |
| REQ-DEV-08 | **マイク／スピーカーいずれも**、常時モニタリングの開始に失敗した場合（デバイスがキャプチャに対応しない、ドライバーが `E_HANDLE` を返す等）、例外を送出せずステータスメッセージで通知し、アプリの動作を継続する。モニタリング開始はデバイス選択プロパティの setter から呼ばれるため、ここで例外を投げると**起動時であればアプリが起動できずクラッシュする** | `AudioCaptureService.StartMicMonitor` / `StartLoopbackMonitor` (いずれも戻り値 `false`), `MainViewModel.OnSelectedCaptureDeviceChanged` / `OnSelectedRenderDeviceChanged` |
| REQ-DEV-09 | マイクの常時モニタリング開始に失敗した場合、OS 側のミュート状態は取得できないため ViewModel への反映を行わない（直前の値を保持する） | `MainViewModel.OnSelectedCaptureDeviceChanged` |

## 2. 録音制御

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-REC-01 | マイクまたはスピーカーの少なくとも一方が選択されていれば録音を開始できる。両方とも未選択の場合は例外を送出する | `AudioCaptureService.StartRecording` |
| REQ-REC-02 | 録音中に再度開始しようとした場合はエラーとする（多重録音防止） | `AudioCaptureService.StartRecording` |
| REQ-REC-03 | 録音は WASAPI Shared Mode で行い、他アプリと同一デバイスを排他せずに共有する | `WasapiCapture { ShareMode = AudioClientShareMode.Shared }` |
| REQ-REC-04 | 録音開始時にファイル名 `yyyyMMdd_HHmmss.mp3` を自動生成し、保存先フォルダに出力する。フォルダが存在しない場合は自動作成する | `AudioCaptureService.StartRecording` |
| REQ-REC-05 | 録音開始時、マイク**およびスピーカー**の常時モニタバッファをクリアし、録音開始時点以降の音声のみを含める | `AudioCaptureService.StartRecording` (`_micBuffer?.ClearBuffer()`, `_loopbackBuffer?.ClearBuffer()`) |
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
| REQ-MIX-07 | データが到着していないソースは自動的に無音（ゼロ埋め）として扱われる（`BufferedWaveProvider.ReadFully = true`） | `AudioCaptureService.StartLoopbackMonitor`, `StartMicMonitor` |

## 4. ミュート制御

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-MUTE-01 | マイクミュート ON 時、キャプチャした音声の代わりに無音データをバッファへ書き込む（ソフトミュート） | `AudioCaptureService` (`_isMicMuted` 分岐) |
| REQ-MUTE-02 | スピーカー（ループバック）ミュート ON 時も同様にソフトミュートを適用し、マイク側には影響しない | `AudioCaptureService` (`_isSpeakerMuted` 分岐) |
| REQ-MUTE-08 | ソフトミュート用の無音バッファはマイク／ループバックで**共有しない**。両キャプチャは別スレッドのコールバックで動作するため、共有すると再確保と読み取りが競合する | `AudioCaptureService` (`_micSilenceBuffer` / `_loopbackSilenceBuffer`) |
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
| REQ-LVL-04 | マイク・スピーカーの**いずれのレベルメーターも、録音の有無に関わらず**デバイスが選択されている間は動作する。録音停止によってメーターを停止・リセットしない | `AudioCaptureService.StartMicMonitor` / `StartLoopbackMonitor` |
| REQ-LVL-05 | ループバックキャプチャは再生中の音声が無いとキャプチャコールバック自体が発生しない（WASAPI の仕様）。最後にデータを受け取ってから一定時間（200ms）が経過した場合、スピーカーのピークレベルを 0 として扱い、メーターが直前の値で固着することを防ぐ | `AudioCaptureService.LoopbackPeakLevel`, `ApplySilenceTimeout` |
| REQ-LVL-06 | デバイスの選択が解除された場合、対応するピークレベルを 0 にリセットする | `AudioCaptureService.StopMicMonitor` / `StopLoopbackMonitor` |

## 6. 設定の永続化

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-CFG-01 | 保存先フォルダ・前回選択デバイス・文字起こし設定（有効化／モデルパス／GPU 使用有無）・無音区間分割の調整値（REQ-CFG-06）を JSON ファイルとして `%APPDATA%\AudioCaptureApp\settings.json` に永続化する | `SettingsService`, `AppSettings` |
| REQ-CFG-02 | 設定ファイルが存在しない、または読み込みに失敗した場合は既定値の `AppSettings` にフォールバックする | `SettingsService.Load` |
| REQ-CFG-03 | 保存先フォルダの既定値は `%USERPROFILE%\Documents\AudioCapture` とする | `AppSettings.OutputFolder` |
| REQ-CFG-04 | Whisper モデルパスの既定値は `%APPDATA%\AudioCaptureApp\models\ggml-small.bin` とする | `AppSettings.WhisperModelPath` |
| REQ-CFG-05 | 保存先フォルダ変更・録音開始・文字起こし設定変更などの操作直後に設定を都度保存する | `MainViewModel.SaveSettings` の各呼び出し箇所 |
| REQ-CFG-06 | 無音区間分割（REQ-TRX-09）に関する 3 つの設定値 `SilenceRmsThreshold`（既定 0.01）・`SilenceMergeGapSeconds`（既定 2.0）・`VoicedPaddingSeconds`（既定 0.2）を保持する。UI は設けず、`settings.json` を直接手編集して変更する。`MainViewModel.SaveSettings` は保存のたびに `AppSettings` を新規生成するのではなく、読み込み済みのインスタンスを更新して保存する。これにより、UI を持たないこれら 3 つの値も、録音開始などの契機（REQ-CFG-05）で保存し直された際に消えずに残る。`settings.json` は手編集され得るため、読み込み時に各値をクランプする。非有限値（NaN・±∞）は既定値にフォールバックし、それ以外の値は許容範囲（`SilenceRmsThreshold` 0.0〜1.0・`SilenceMergeGapSeconds` 0.0〜20.0・`VoicedPaddingSeconds` 0.0〜5.0）にクランプする。設定キー名とオプション型のプロパティ名の対応は `SilenceRmsThreshold` → `SilenceCutOptions.RmsThreshold`、`SilenceMergeGapSeconds` → `MergeGapSeconds`、`VoicedPaddingSeconds` → `PaddingSeconds` である | `AppSettings`, `SilenceCutOptions`, `MainViewModel.SaveSettings` |

## 7. 文字起こし（共通基盤）

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-01 | Whisper GGML モデルファイルを指定して読み込める。存在しないパス、および Whisper モデルとして読み込めないファイル（破損・非対応形式）の場合は失敗を返す。`WhisperFactory.FromPath` はネイティブ側の読み込み失敗を例外にせずファクトリを返すため、読み込み後に `CreateBuilder()` を 1 度呼んで失敗を確定させる | `TranscriptionService.LoadModel` |
| REQ-TRX-02 | ネイティブランタイムは GPU 優先順（CUDA > Vulkan > CoreML > OpenVino > CPU）で読み込み、GPU が利用可能かは「読み込まれた種別が GPU 版であること」**かつ**「ネイティブ側で GPU バックエンドが実際に登録されたこと（`whisper_init_with_params_no_state: backends` が 2 以上）」の両方で判定する。GPU 版ランタイム DLL はデバイスが無くても読み込めるため、種別だけでは判定できない。**この読み込みはプロセス内で 1 度しか行われない**（Whisper.net が `WhisperFactory.LibraryLoaded` を `static Lazy` で保持し、`RuntimeOptions.RuntimeLibraryOrder` を初回のロード時にしか参照しないため）。したがって 2 回目以降の `LoadModel` で順序を変えても読み込み直されない | `TranscriptionService.LoadModel`, `GpuPreferredOrder` |
| REQ-TRX-03 | GPU 実行と CPU 実行の切り替えは、ランタイムの読み込み直しではなく `WhisperFactoryOptions.UseGpu`（whisper.cpp の `whisper_context_params.use_gpu` に対応）で行う。GPU 版ランタイムを読み込んだままでも `UseGpu = false` なら計算は CPU で行われる | `TranscriptionService.LoadModel` |
| REQ-TRX-04 | モデルパスが設定されていれば、ライブ文字起こしの ON/OFF に関わらず常にモデルをロードする（ファイル文字起こしはライブ設定と独立して動作するため） | `MainViewModel` コンストラクタ, `TryLoadWhisperModel` |
| REQ-TRX-05 | 入力音声はダウンミックス（ステレオ→モノラル）・1 次 IIR ローパスフィルタ・線形リサンプリングにより 16kHz モノラルへ変換してから Whisper に渡す | `TranscriptionService.DownmixResampleAppend` |
| REQ-TRX-06 | 無音判定はチャンクを 100ms の窓に区切って行う。各窓の RMS が設定値（既定 -40dB 相当 = 0.01）**以上**であればその窓を「有声」と判定する。チャンク全体の平均で判定しないのは、長い無音に埋もれた短い発話がならされて捨てられるため | `TranscriptionService.SplitVoicedRegions` |
| REQ-TRX-07 | 文字起こし結果は `[開始時刻 - 終了時刻] [ラベル] テキスト` 形式の行としてテキストファイルに追記する。話者ダイアライゼーションが有効なときに限り、ラベルの直後に話者欄が入り `[開始時刻 - 終了時刻] [ラベル] [話者N] テキスト` となる（REQ-TRX-DIA-06）。無効時の行は従来と 1 文字も変わらない。**ライブ文字起こしでは、チャンクの区間ループがキャンセル・例外で途中終了した場合でも、その時点までに確定していた行を必ず追記する。**確定した行は `SegmentTranscribed` イベントで既に画面へ出ているため、書き出さないと画面と `.txt` が食い違うためである。追記そのものに失敗した場合は `Error` イベントで通知し、ワーカースレッドは継続する。なおファイル文字起こし側は逆に、キャンセル時は出力ファイルごと削除して部分結果を残さない（REQ-TRX-FILE-07）。ライブの `.txt` は録音中に追記され続けるログであるのに対し、ファイル側の `.transcript.txt` は完結した成果物であるという性質の違いによる | `TranscriptionService.ProcessChunk`, `AppendTranscriptLines`, `ProcessFileChunkAsync` |
| REQ-TRX-08 | Whisper へ渡す区間（チャンク全体を 1 区間とする場合を含む）が 1 秒未満の場合、末尾を無音で埋めて 1 秒に伸ばす。whisper.cpp は 0.2 秒未満の入力に対してセグメントを 1 件も返さないため（実測） | `TranscriptionService.PadToMinimum` |
| REQ-TRX-09 | チャンクを Whisper に渡す前に有声区間へ分割し、無音区間そのものは渡さない（ハルシネーション防止）。<br>①100ms 窓ごとに RMS が閾値以上かで有声／無音を判定する（REQ-TRX-06）<br>②連続する有声窓をひとつの区間にまとめる<br>③区間同士の間にある無音（パディング前の生の隙間）が `MergeGapSeconds`（既定 2.0 秒）未満であれば区間を結合する<br>④**0.2 秒以上続く有声ラン（③で結合する前の、①②で得た連続有声窓のかたまり）を 1 本も含まない区間を捨てる**。結合後の区間長で判定してはならない。結合後の長さは内部に取り込んだ無音を含むため、0.1 秒の物音が結合幅の中に 2 つあるだけで 0.2 秒を超え、中身の大半が無音の区間が生き残ってしまう。③の結合は「実体のある発話へ、その前後の短い断片を貼り付ける」ための手順であり、貼り付ける先の発話（＝0.2 秒以上続くラン）が無いなら結合結果に残すべきものは無い。結合が起きていない区間ではラン＝区間なので、判定は「区間長が 0.2 秒未満なら捨てる」と一致する<br>⑤各区間の前後を `PaddingSeconds`（既定 0.2 秒）だけ広げ、チャンク範囲でクランプしたうえで、接触した区間はさらに結合する<br>⑥パディング後の区間の合計がチャンク全体の 90% 以上を占める場合は、分割してもほとんど無音を削減できず Whisper 呼び出し回数だけが増えるため、チャンク全体を単一区間として返す。<br>**④（足切り）は⑤（パディング）より必ず先に行う**。順序を逆にすると、0.1 秒（＝窓 1 つ分。窓量子化により、これが存在しうる最短のラン）のクリックノイズが前後 0.2 秒ずつ広がって 0.5 秒のランになり、0.2 秒のカットオフを生き残ってしまうため。落としたいのは「元々短い音」であって「余白を足したら長くなった音」ではない。<br>有声区間が 1 件も無い場合は Whisper を呼ばず、ファイルへの書き込みも行わない。<br>分割された各区間は個別に Whisper へ渡し、区間ごとに自身のチャンク内開始オフセットを保持することでタイムスタンプの整合性を保つ。渡す前に各区間へ REQ-TRX-08 の最小長パディングを適用する。<br>ライブ文字起こし・ファイル文字起こしの両方に適用する | `TranscriptionService.SplitVoicedRegions`, `ProcessChunk`, `ProcessFileChunkAsync` |

## 8. ライブ文字起こし（録音中）

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-LIVE-01 | 「録音中にライブ文字起こしする」が ON かつモデルロード済みの場合のみ、録音サービスに文字起こしサービスを接続する | `MainViewModel.OnTranscriptionEnabledChanged` |
| REQ-TRX-LIVE-02 | 録音開始時、マイク・スピーカーそれぞれを音声ソースとして登録し（`マイク`/`スピーカー` ラベル）、出力パスは録音 MP3 と同名の `.txt` とする | `AudioCaptureService.StartRecording`, `TranscriptionService.RegisterSource/StartSession` |
| REQ-TRX-LIVE-03 | 録音中、マイク／スピーカーの音声チャンクをそれぞれ非同期にバッファへ蓄積する | `AudioCaptureService` DataAvailable ハンドラ, `TranscriptionService.AddSamples` |
| REQ-TRX-LIVE-04 | バックグラウンドスレッドが 1 秒周期でポーリングし、各ソースのバッファから**確定条件を満たしたチャンク**を取り出して Whisper に投入する。確定条件は ①20 秒分（16kHz 換算）たまった（REQ-TRX-LIVE-10）②末尾に発話終了とみなせる無音が積まれた（REQ-TRX-LIVE-13）③供給が途絶えた（REQ-TRX-LIVE-12）の 3 つで、**この優先順で判定する**。①を最優先に置くのは、1 回の Whisper 呼び出しを際限なく長くしないための上限だからである（REQ-TRX-LIVE-10）。1 回のポーリングで取り出せるチャンクをすべて処理するが、**停止要求（`_isRunning == false`）を検知したら直ちに打ち切る** | `TranscriptionService.TranscriptionLoop`, `TakeNextChunk`, `ChunkTakeCount` |
| REQ-TRX-LIVE-05 | 録音停止時、残りバッファ（**0.2 秒**以上ある場合）を処理してからセッションを終了する | `TranscriptionService.StopSession`, `TranscriptionLoop` 終端処理 |
| REQ-TRX-LIVE-10 | 1 回の Whisper 呼び出しに渡すのは**最大 20 秒分**とする。バッファ全体を渡すと、文字起こしが追いつかず滞留した際に 1 回の呼び出しが数分分になり、キャンセル不能になって停止処理がタイムアウトするため | `TranscriptionService.TakeNextChunk` |
| REQ-TRX-LIVE-12 | 20 秒分に達していなくても、音声の**供給が 5 秒以上途絶えている**場合はチャンクとして確定する（0.2 秒未満の断片は除く）。途絶時間は「最後にサンプルを受け取った時刻からの経過時間」で測る。この契機が無いと、ミュートや再生停止で供給が止まったソースのバッファは「次のパケットが到着してギャップ分割が発火するまで」書き出されず、書き出し遅延が無制限になる。**バッファ先頭サンプルの滞留時間で測ってはならない。**滞留時間は「供給の途絶時間 ＋ バッファ長」であり、供給が続いている限りバッファ長とほぼ等しくなる。5 秒で判定すると「バッファ長 5 秒で確定」＝チャンク長を 5 秒に固定したのと同じ挙動になり、発話の途中で機械的に分断される（この同値性のため、閾値が 20 秒だった頃はこの契機が REQ-TRX-LIVE-10 に吸収されて連続供給下では常に空振りしていた。T129） | `TranscriptionService.ChunkTakeCount`, `StaleSupplyIdle` |
| REQ-TRX-LIVE-13 | 20 秒分に達していなくても、バッファ**末尾に `MergeGapSeconds`（既定 2.0 秒）以上の連続した無音**があり、**かつバッファ内に有声窓が 1 つ以上ある**場合はチャンクとして確定する（発話終了の検出）。無音判定の窓長と閾値は REQ-TRX-06 と同一とし、窓は**バッファ末尾に揃えて**敷く（先頭揃えだと末尾の最大 99ms が端数窓に紛れて判定がぶれるため）。先頭側に余る 1 窓未満の端数も実長で 1 つの窓として評価する（飛ばすと、発話がバッファ先頭の端数に収まっている場合に「全体が無音」と誤判定して確定を取りこぼす）。保持時間に `MergeGapSeconds` と**同じ値**を使うのは、この値が REQ-TRX-09 ③で「発話の切れ目」を定義している値そのものだからである。同じ値で確定すれば確定チャンクは有声区間ちょうど 1 個を含む形になり、1 発話が複数行に割れず、Whisper の呼び出し回数もこの契機の導入前と変わらない。**バッファ全体が無音の場合は確定しない**（有声窓が無いため）。その場合は 20 秒分たまった時点で切り出され、有声区間 0 件として Whisper を呼ばずに捨てられる（REQ-TRX-09）。また**末尾無音が 0 サンプルの場合も確定しない**。`MergeGapSeconds` に 0 が設定されたときに、末尾が有声のまま＝発話の途中で毎ポーリング確定してしまうのを防ぐため | `TranscriptionService.ChunkTakeCount`, `TrailingSilenceSamples` |
| REQ-TRX-LIVE-11 | セッション停止時、ワーカースレッドの終了を最大 30 秒待ち、タイムアウトしたらキャンセルしてさらに 10 秒待つ。**ワーカーが終了していない場合は `WhisperProcessor` の破棄を見送る**（処理中の破棄は Whisper.net が例外を投げ、未処理例外としてプロセスを終了させるため）。破棄時の例外も画面表示に変換し、いかなる場合もプロセスを落とさない | `TranscriptionService.StopSession`, `DisposeProcessorSafely` |
| REQ-TRX-LIVE-06 | 記録する時刻は、投入されたサンプル数の累積ではなく**セッション開始からの実経過時間**（単調増加クロック）から求める。ミュート中や再生停止中は音声が供給されず、累積では時計が止まって実時刻と乖離するため | `TranscriptionService._sessionClock`, `PendingChunk.StartElapsed` |
| REQ-TRX-LIVE-07 | 音声の供給が 500ms を超えて途切れた場合、そこまでのバッファを 20 秒未満でも 1 チャンクとして確定し、次チャンクの基準時刻を打ち直す。これによりミュート／再生停止をまたいでも時刻が実時刻に追従する | `TranscriptionService.AddSamples`, `ShouldSplitOnGap` |
| REQ-TRX-LIVE-08 | チャンク先頭の時刻は、バッファ末尾の経過時間からバッファ長を差し引いて求める。末尾を毎パケット実時間へ再アンカーするため、リサンプル誤差が累積しない | `TranscriptionService.ChunkStartElapsed` |
| REQ-TRX-LIVE-09 | ギャップでチャンクを分割する際、リサンプラとローパスフィルタの状態もリセットする（不連続な音声を地続きとして扱わないため） | `TranscriptionService.AddSamples` |

## 9. ファイルからの文字起こし

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-FILE-01 | モデルロード済みの場合、ファイル選択ダイアログ（`*.wav;*.mp3`）から音声ファイルを選べる。選択後は直ちに処理を始めず、オプション指定ダイアログ（REQ-TRX-FILE-09）を表示する | `MainViewModel.TranscribeFromFile` |
| REQ-TRX-FILE-02 | 対応拡張子（.wav / .mp3）のファイルをウィンドウ上の文字起こしグループへドラッグ＆ドロップして文字起こしを開始できる。この経路でもオプション指定ダイアログ（REQ-TRX-FILE-09）を経由する | `MainWindow.xaml.cs` (`Drop` イベント), `MainViewModel.TranscribeDroppedFile` |
| REQ-TRX-FILE-03 | 非対応の拡張子がドロップされた場合はエラーメッセージを表示し処理しない | `MainViewModel.TranscribeDroppedFile`, `IsSupportedAudioExtension` |
| REQ-TRX-FILE-04 | ドラッグオーバー中、ドロップ可能かどうかに応じてオーバーレイ表示を切り替える | `MainWindow.xaml.cs` (`DragOver`/`DragLeave`) |
| REQ-TRX-FILE-05 | 出力ファイルは `{入力ファイル名}.transcript.txt`（録音時生成の `.txt` と名前衝突しない命名）として同フォルダに保存する | `TranscriptionService.BuildTranscriptPath` |
| REQ-TRX-FILE-06 | 処理中は進捗（フェーズ名・処理済み時間／総時間）を UI に表示する。オプション指定ダイアログ（REQ-TRX-FILE-09）には百分率の進捗バーも表示する。進捗は常に**ファイル先頭を基準**とし、REQ-TRX-FILE-10 の開始時刻を足さない（残り時間の目安であって時刻ではないため）。話者ダイアライゼーションが有効なときは処理が「話者識別中」→「処理中」の 2 フェーズになり、進捗はフェーズごとに 0% から進む。どちらのフェーズでも「処理済み時間」はそのフェーズが実際に読み終えたファイル内の位置であり、フェーズ名とセットで表示することで意味を保つ。無効時はフェーズが「処理中」1 つだけなので表示は従来と変わらない | `MainViewModel.RunFileTranscriptionAsync`, `FileTranscriptionProgressFor`, `IProgress<FileTranscriptionProgress>` |
| REQ-TRX-FILE-07 | 処理中に「中止」操作でキャンセルできる。キャンセル時は生成中の出力ファイルを削除し、部分結果を残さない。**削除してよいのはその実行が作りかけたファイルだけである。**話者ダイアライゼーション有効時はマージが終わるまで出力ファイルを開かないため、中止時点では未作成であり削除しない（無条件に削除すると前回成功したときの `.transcript.txt` を巻き添えにする）。また有効時の書き出しは推論がすべて終わった後の確定処理なので途中でキャンセルせず、全行書くか一行も書かないかのどちらかにする。「中止」はオプション指定ダイアログとメインウィンドウの双方に置く（REQ-TRX-FILE-13 でダイアログを閉じても処理は続くため） | `MainViewModel.CancelFileTranscription`, `TranscriptionService.TranscribeFileAsync` (catch `OperationCanceledException`) |
| REQ-TRX-FILE-08 | ファイル文字起こし処理は UI スレッドをブロックしないようバックグラウンドスレッドで実行する | `MainViewModel.RunFileTranscriptionAsync` (`Task.Run`) |
| REQ-TRX-FILE-09 | ファイルが決まったら（選択・ドロップのいずれでも）、処理を始める前に**モーダルダイアログ**を表示する。ダイアログは対象ファイル名・開始時刻の入力欄（REQ-TRX-FILE-10）・進捗表示・「開始」「キャンセル」／処理中は「中止」を持つ。ダイアログは `MainViewModel` を `DataContext` として共有する状態レスな View であり、生成・表示は `MainWindow` が行う（[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md)） | `FileTranscriptionOptionsWindow`, `MainWindow.xaml.cs`, `MainViewModel.FileTranscriptionRequested` |
| REQ-TRX-FILE-10 | 開始時刻を `h:mm` または `hh:mm`（24 時間表記）で指定できる。指定した時刻を出力行のタイムスタンプの起点にする。**空欄なら未指定**とし、従来どおりファイル先頭を `00:00:00` として出力する。**入力欄の初期値は REQ-TRX-FILE-15 が推定して入れる**（推定できなければ空欄）。書式が不正な間は「開始」を無効化し、理由をダイアログ上に表示する。開始時刻とファイル長の合計が 24 時間を超えた場合は翌日の時刻として折り返す（`23:00` 開始の 2 時間後は `01:00:00`） | `MainViewModel.TryParseStartTime`, `TranscriptionService.TranscribeFileAsync` (`startOffset`) |
| REQ-TRX-FILE-11 | ダイアログの「開始」を押すと、同じダイアログが進捗表示へ切り替わる（別ウィンドウを開き直さない） | `FileTranscriptionOptionsWindow.xaml` (`IsTranscribingFile` の DataTrigger) |
| REQ-TRX-FILE-12 | 処理が終わったら（完了・失敗・中止のいずれでも）ダイアログを自動的に閉じる。結果はメインウィンドウのステータスバーに表示する | `FileTranscriptionOptionsWindow.xaml.cs` |
| REQ-TRX-FILE-13 | 処理中にダイアログを閉じても処理は継続する。メインウィンドウ側の進捗表示と「中止」ボタンが引き継ぐ | `MainWindow.xaml` (既存の進捗行), `MainViewModel.IsTranscribingFile` |
| REQ-TRX-FILE-15 | 開始時刻（REQ-TRX-FILE-10）の初期値を対象ファイルから推定して入力欄に入れる。取得元は **①ファイル名（本アプリが録音した `yyyyMMdd_HHmmss` 形式。拡張子を除いた名前全体が一致する場合のみ）→ ②ファイルの作成日時 → ③最終更新日時 − 音声の長さ** の順に試し、最初に取れたものを採る。ただし **`作成日時 > 最終更新日時` のときは②を採らず③へ落とす** — この逆転はファイルがコピー・移動され、作成日時が「コピーした日時」に書き換わったことを示すためである。③の音声長は**無音カット後ではなくファイル全長**を使い、**③に落ちたときだけ読む**（`AudioFileReader` は MP3 のフレーム表を作るためにファイル全体を走査するため）。値は入力欄の書式に合わせて `HH:mm` へ**分単位で切り捨てる**。いずれの取得元も**推定にすぎない**ため、どれを使ったかをダイアログ上に 1 行で表示し、利用者がそのまま消せるようにする（空欄＝未指定は従来どおり有効）。入力欄が編集されたらその表示は消す。推定できなかった場合はエラーとせず空欄のままにする | `MainViewModel.InferStartTime` / `TryParseRecordedFileNameTime` / `FileTranscriptionStartTimeHint`, `TranscriptionService.TryGetAudioDuration`, `FileTranscriptionOptionsWindow.xaml` |

## 10. GPU 使用切り替え

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-GPU-01 | 「文字起こしに GPU を使用する」設定を保持し、既定値は ON（true）とする | `AppSettings.UseGpuForTranscription` |
| REQ-GPU-02 | モデル読み込みの結果、GPU が利用不可（REQ-TRX-02 の判定）と判明した場合は設定を強制的に OFF にし、UI 上でも操作不可（無効化）にする。判定はユーザー設定の ON/OFF に依存しないため、GPU 使用 OFF の状態で起動しても正しく再判定される | `MainViewModel.TryLoadWhisperModel`, `GpuAvailable`, `CanToggleGpu` |
| REQ-GPU-03 | GPU 使用設定を切り替えると、現在のモデルを破棄し、新しい `UseGpu` 値（REQ-TRX-03）でファクトリを作り直す | `MainViewModel.OnUseGpuForTranscriptionChanged` → `TryLoadWhisperModel` |
| REQ-GPU-04 | 録音中・録音停止処理中・ファイル文字起こし中は GPU 使用設定の切り替えを禁止する | `MainViewModel.CanToggleGpu` (`IsNotBusy && GpuAvailable`) |
| REQ-GPU-05 | 実際の実行先をステータスメッセージとして通知する。GPU 実行時はランタイム種別を併記して `GPU (Vulkan)` のように、CPU 実行時は `CPU` とする。実行先は**モデルの重みが実際に載ったバックエンド**（ネイティブログの `whisper_model_load: <X> total size` の `<X>`）から決める。`RuntimeOptions.LoadedLibrary` は読み込んだランタイム DLL の種別、`UseGpu` は要求にすぎず、どちらも実行先を表さないため単独で使ってはならない | `TranscriptionService.RuntimeInfo` イベント |

## 11. 成果物フォルダを開く

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-OPEN-01 | 録音の停止完了時、および音声ファイルからの文字起こし完了時に、直近の成果物のパスを保持する。録音時はライブ文字起こしが有効なら `.txt`、無効なら `.mp3`。ファイル文字起こし時は `.transcript.txt` | `MainViewModel.LastResultPath` |
| REQ-OPEN-02 | 直近の成果物が存在する場合、その**フォルダをエクスプローラーで開き、成果物ファイルを選択状態にする**（`explorer.exe /select,"<path>"`） | `MainViewModel.OpenResultFolder`, `BuildExplorerArguments` |
| REQ-OPEN-03 | 成果物ファイルが既に削除されている場合は親フォルダを開く。親フォルダも存在しない場合は何もせずステータスメッセージで通知する | `MainViewModel.BuildExplorerArguments` |
| REQ-OPEN-04 | 直近の成果物が未設定（起動直後・録音データなし）の場合、操作を無効化する | `MainViewModel.CanOpenResultFolder` |
| REQ-OPEN-05 | 録音中・録音停止処理中・ファイル文字起こし中は操作を無効化する。保持しているのは**それ以前の**成果物であり、進行中の作業の成果物ではないため | `MainViewModel.CanOpenResultFolder` (`IsNotBusy`) |

> 録音の保存先は `OutputFolder`、ファイル文字起こしの出力先は入力ファイルと同じフォルダであり
> 両者は一致しない。そのため「設定上の保存先」ではなく **「直近の成果物の場所」** を開く。

## 12. 文字起こし表示ウィンドウ

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-LIVEVIEW-01 | 文字起こしされた行を時刻順に表示するサブウィンドウを開ける。導線は文字起こし設定グループのヘッダーに置いたボタン「表示」とし、録音中・処理中でも押せる（見るための窓のため無効化しない） | `MainWindow.xaml`, `MainViewModel.ShowLiveTranscript`, `LiveTranscriptWindow` |
| REQ-LIVEVIEW-02 | 初期サイズは 480x240 でリサイズ可能。文字サイズは 9pt とする | `LiveTranscriptWindow.xaml` |
| REQ-LIVEVIEW-03 | 表示するのは `TranscriptionService.SegmentTranscribed` が通知した行であり、**ライブ文字起こしとファイル文字起こしの両方**を含む。行頭のラベル（`[マイク]` / `[スピーカー]` / `[ファイル]`）で区別できる。イベントは文字起こしワーカースレッドから発火するため、`Dispatcher.BeginInvoke` を経由して UI スレッドで追加する（NFR-01） | `MainViewModel` コンストラクタ, `LiveTranscriptLines` |
| REQ-LIVEVIEW-04 | 表示行数の上限は 100 行とし、超えたら**古い行から**捨てる。捨てられるのは表示のみで、テキストファイルには全行が残る | `MainViewModel.AppendLiveTranscriptLine`, `MaxLiveTranscriptLines` |
| REQ-LIVEVIEW-05 | 録音を停止してもウィンドウは閉じない。表示中の行も消さない。メインウィンドウを閉じたとき（プロセス終了）に一緒に閉じる。後者は `Owner` に `MainWindow` を設定することで WPF の既定動作として実現し、追随処理を自前で書かない。表示内容を消す唯一の契機は次の録音の開始である（REQ-LIVEVIEW-08） | `MainWindow.xaml.cs` (`Owner = this`) |
| REQ-LIVEVIEW-06 | ウィンドウは同時に 1 つだけ開く。既に開いている状態で操作された場合は手前に出す（`Activate`）。新しい行が届いたら最新行までスクロールする。**このスクロールを `CollectionChanged` ハンドラーの中で同期的に行ってはならない**（次行 REQ-LIVEVIEW-07） | `MainWindow.xaml.cs`, `LiveTranscriptWindow.xaml.cs` |
| REQ-LIVEVIEW-07 | 最新行へのスクロールは `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` で**後回しにして**実行する。`CollectionChanged` ハンドラーの中から `ScrollIntoView` を同期的に呼ぶと `ItemsControl.OnBringItemIntoView` → `UpdateLayout()` が走り、まだ変更を処理し終えていない `ItemContainerGenerator` の累計カウントと `ItemCollection.Count` が食い違って `Verify()` が `InvalidOperationException`（「ItemsControl が項目のソースと一致していません」）を投げる。UI スレッドの未処理例外となりプロセスが落ちる（T128 で実際に発生）。ウィンドウのコンストラクターは XAML バインディングより先に `CollectionChanged` を購読するため、自分のハンドラーは WPF 側のジェネレーターより**先に**呼ばれる。この順序は WPF の内部都合であり、こちらから制御できない | `LiveTranscriptWindow.OnLinesChanged` |
| REQ-LIVEVIEW-08 | **録音の開始に成功した時点で表示内容をクリアする。**前のセッションの行が新しいセッションの行に混ざらないようにするため。録音の開始に失敗した場合（`AudioCaptureService.StartRecording` が例外を送出した場合）はクリアしない。開始できなかったのに直前の内容が消えると、失敗の前後を見比べられなくなるためである。ファイル文字起こしの開始ではクリアしない（別の導線であり、ライブの結果を見ながらファイルを流す使い方を壊さないため）。手動でクリアする手段は設けない | `MainViewModel.StartRecording` |
| REQ-LIVEVIEW-09 | `SegmentTranscribed` は短時間に大量発火することがある（話者ダイアライゼーション有効時のファイル文字起こしは、話者の割り当てが全体で確定してからでないと 1 行も出せないため、**書き出しの最後に全行をまとめて発火する**。1 時間の会議なら 1,500〜2,100 行）。この経路で行を 1 行ずつ `Dispatcher.BeginInvoke` へ積むと、そのたびに `CollectionChanged` → レイアウト → `ScrollIntoView`（REQ-LIVEVIEW-07）が走り、**大半は表示上限（REQ-LIVEVIEW-04）で直後に捨てられる**。そこで、届いた行はいったんキューへ溜め、UI スレッドへは**まとめて 1 回**で流し込み、そのとき**表示上限を超えるぶんは `ObservableCollection` へ追加せずに捨てる**。追加してから捨てても最終状態は同じであり、追加の費用だけが減る。ライブ文字起こしのように 1 行ずつ届く場合は 1 回の発火につき 1 行しか溜まらないため、挙動は従来と変わらない。実測（2,100 行・表示ウィンドウを開いた状態）: 1 行ずつ **665〜735ms**／UI の詰まり最大 **177ms** → 本方式 **143ms**／最大 **99ms**。**「まとめて 1 回の `BeginInvoke` にする」だけでは効かない**（実測 667ms）— 費用は Dispatcher のキューではなく 1 行ごとのレイアウトだからである | `MainViewModel.QueueLiveTranscriptLine`, `FlushLiveTranscriptLines`, `AppendLiveTranscriptLines` |

> ウィンドウは自前の状態を持たず、`MainWindow` と同じ `MainViewModel` インスタンスを
> `DataContext` として共有する（[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md)）。

## 13. 話者ダイアライゼーション（ファイル文字起こし）

同一音声内で話者を `話者1` / `話者2` … と区別する。**実在の人物名への変換（Speaker Identification）は行わない。**
採用理由と却下した案は [ADR-0003](../adr/0003-speaker-diarization-with-sherpa-onnx.md)。

| ID | 要件 | 実装箇所 |
|---|---|---|
| REQ-TRX-DIA-01 | 話者ダイアライゼーションは sherpa-onnx の `OfflineSpeakerDiarization`（pyannote 系 segmentation モデル ＋ 話者埋め込みモデル）で完全ローカルに実行する。外部 API 呼び出し・モデルの実行時ダウンロード・音声や文字起こし内容の外部送信は一切行わない。ネットワーク接続が無い環境でも動作する | `SpeakerDiarizationService` |
| REQ-TRX-DIA-02 | **適用対象はファイル文字起こし（REQ-TRX-FILE-*）のみ**とする。録音中のライブ文字起こしには適用しない。sherpa-onnx の Diarization API は音声全体を 1 つの配列で受け取る設計であり、チャンク単位のストリーミング処理に載せられないためである。加えて話者 ID は 1 回の Diarization 実行の中でのみ有効なので、チャンクごとに実行すると「同一話者が再登場したら同じ ID」を満たせない | `TranscriptionService.TranscribeFileAsync` |
| REQ-TRX-DIA-03 | 既定は**無効**（`SpeakerDiarizationEnabled = false`）。無効時のファイル文字起こしは従来どおりストリーミングで処理し、出力行も従来と同一とする。有効化は `settings.json` で行う（本要件の範囲では UI から切り替えない） | `AppSettings`, `MainViewModel` コンストラクタ |
| REQ-TRX-DIA-04 | Whisper による文字起こしと Diarization は互いに依存させず、**同じ 16kHz モノラル音声をそれぞれ独立に解析**し、後段でタイムラインを突き合わせて統合する。Diarization の結果で音声を切り分けてから Whisper に渡す構成にはしない（Whisper の認識コンテキストが失われるため） | `TranscriptionService.TranscribeFileWithDiarizationAsync` |
| REQ-TRX-DIA-05 | 文字起こしセグメントへの話者割り当ては、**開始時刻の比較ではなく時間的な重複長**で決める。`overlap = max(0, min(t.End, s.End) - max(t.Start, s.Start))` が最大の話者を選ぶ。重複長が等しい話者が複数いる場合は**話者 ID が小さい方**を選ぶ（結果を決定的にするため）。重複する話者区間が 1 つも無い場合は**話者不明**とし、直前の話者を引き継がない。**発話時間帯（REQ-TRX-DIA-13）が分かっている場合は、セグメント全体ではなくその時間帯の合計で重複長を測る** | `TranscriptDiarizationMerger.Merge` |
| REQ-TRX-DIA-06 | 出力行の話者欄は 1 始まりの表示番号で `[話者1]` と書く。内部の話者 ID は sherpa-onnx が返す 0 始まりの値をそのまま保持し、表示時にのみ +1 する。話者を決められなかったセグメントは `[話者不明]` と書く | `TranscriptDiarizationMerger.FormatSpeaker` |
| REQ-TRX-DIA-07 | **話者数を固定値として既定にしない。** 話者数が未知の場合は sherpa-onnx のクラスタリング閾値（`SpeakerClusteringThreshold`、既定 0.5）を使う。設定で話者数（`KnownSpeakerCount`）が 1 以上に指定されている場合に限り `NumClusters` を使う。未指定（`null` または 0 以下）なら閾値を使う | `SpeakerDiarizationOptions` |
| REQ-TRX-DIA-08 | モデルのパスをコードへ埋め込まない。segmentation モデルと埋め込みモデルのパスは設定（`SpeakerSegmentationModelPath` / `SpeakerEmbeddingModelPath`）で与える。**処理を開始する前に**両ファイルの存在を確認し、無ければどちらのモデルかが分かるエラーを出して処理を中止する。この事前確認は省略できない — sherpa-onnx のネイティブ側はモデルが無いと NULL ハンドルを返すが C# ラッパーがそれを検査しないため、そのまま使うとアクセス違反（0xC0000005）で**プロセスが即死する**（.NET の catch を通らない） | `SpeakerDiarizationService.EnsureLoaded` |
| REQ-TRX-DIA-09 | Diarization へ渡す音声のサンプリングレートが、読み込んだ segmentation モデルの要求レート（`OfflineSpeakerDiarization.SampleRate`）と一致することを実行前に確認する。**不一致を暗黙に許容しない**（一致しなければエラーとして中止する）。本アプリは REQ-TRX-05 により常に 16kHz モノラルへ正規化しているため、通常は一致する | `SpeakerDiarizationService.Diarize` |
| REQ-TRX-DIA-10 | 読み込んだモデルはアプリの実行中を通じて使い回し、ファイルごとに読み込み直さない。`OfflineSpeakerDiarization` はスレッド安全性が保証されていないため、`Diarize` の呼び出しは `lock` で直列化する。ネイティブリソースは `SpeakerDiarizationService.Dispose` で確実に解放する | `SpeakerDiarizationService` |
| REQ-TRX-DIA-11 | Diarization の失敗（モデル未配置・モデル破損・初期化失敗・サンプリングレート不一致・推論失敗）は、どの段階で何が起きたかが分かるメッセージで `Error` イベントに通知する。音声の内容や文字起こし本文はエラーログに出さない。Diarization が失敗した場合は**その旨を通知したうえで文字起こし自体は中止する**（話者欄が黙って欠けた成果物を作らないため）。`SpeakerDiarizationService` が `SpeakerDiarizationException` を送出し、`TranscribeFileAsync` の catch がそれを `Error` イベントへ変換して `false` を返す | `SpeakerDiarizationService`, `TranscriptionService.TranscribeFileAsync` (catch) |
| REQ-TRX-DIA-13 | 重複長は、**文字起こしセグメントの時間範囲そのものではなく、その中で実際に発話が存在する時間帯の合計**で測る。Whisper のセグメントは、REQ-TRX-08 の最小長パディングで足した無音や、発話が終わった後の余白まで含んで伸びることがあり、その余白が隣の話者の時間帯にかかると誤った話者へ寄る。発話時間帯は Whisper のトークン単位のタイムスタンプ（`WithTokenTimestamps()`。`[_BEG_]` / `[_TT_nnn]` 等の特殊トークンと長さ 0 のトークンは除く）から求め、重なり合うものは 1 つにまとめる。**DTW による整列（`UseDtwTimeStamps`）は使わない** — 有効化にはモデルごとに対応した alignment heads プリセットの指定が要るのに対し、本アプリはモデルパスを設定で差し替えられるため対応付けを保証できない。実測でも DTW なしのトークン時刻で十分な精度が得られている。トークンが 1 つも得られなかった場合はセグメントの時間範囲をそのまま使う（従来動作へ縮退する） | `TranscriptionService.CollectTranscriptSegmentsAsync`, `TranscriptDiarizationMerger.Merge` |
| REQ-TRX-DIA-12 | キャンセルは Diarization の**開始前と完了後**にのみ評価する。sherpa-onnx の進捗コールバック（`ProcessWithCallback`）は進捗の報告にだけ使い、そこからネイティブ処理を中断させることはしない。ネイティブ処理の強制停止はネイティブリソースの破棄漏れとプロセスクラッシュを招くためである。したがって Diarization 実行中の「中止」操作は、その 1 回の推論が終わるまで反映されない | `SpeakerDiarizationService.Diarize` |
| REQ-TRX-DIA-14 | 推論スレッド数（`SpeakerDiarizationThreads`）の既定は **論理コア数と 4 の小さいほう**（`min(4, Environment.ProcessorCount)`）とする。スレッド数は**結果を変えない** — 実測で 1〜20 スレッドのすべてが話者区間 38 本を開始・終了・ID まで完全に一致させた（速度だけの設定である）。上限を 4 に置くのは実測で 4 を超えても速くならず、8 を超えると遅くなるためである（2 分 42 秒の音声で 1→4 スレッドは 28.3→22.3 秒、20 スレッドでは 38.4 秒）。効果が大きいのは他の処理と CPU を奪い合うときで、16 スレッド分の負荷の下では 1 スレッド 85 秒に対し 4 スレッド 47 秒だった。設定で 1〜16 に上書きできる。**既存の `settings.json` に書かれている値は書き換えない**（既定値の変更は新規の設定ファイルにのみ効く） | `AppSettings`, `SpeakerDiarizationOptions` |

> **割り当ての粒度:** 話者を割り当てる単位は Whisper の**セグメント**である。1 つのセグメントの途中で話者が切り替わる場合、そのセグメント全体が重複の長い 1 人へ寄る。
> REQ-TRX-DIA-13 は重複の測り方を精密にするだけで、テキストの分割は行わない。
> テキストを分割できないのは、Whisper の BPE トークンがマルチバイト文字の途中で切れると
> `WhisperToken.Text` の時点で U+FFFD へ置換され、**元のバイトを復元できない**ためである
> （実測で U+FFFD を含むセグメントは音声 5 本すべてで 37〜81% にのぼる。素朴に連結すると大半が文字化けする）。
> **分割は当面行わない。** T141 で頻度（セグメント内に 0.5 秒以上の話者境界があるのは 0〜21.3%。ただし
> 話者 ID の断片化により、その大半は別人への交代ではなく同一人物の断片の境目である）と実装コストを
> 実測したうえで見送った。

> **検出の粒度（T142 の実測）:** 相槌や短い割り込みは、**そもそも話者区間が生成されないことがある**。
> 実測では、13.9 秒の話者区間 1 本の中に別話者の割り込みが 2 か所あったが、
> どちらにも対応する区間が生成されなかった。これは後処理の最小長（`MinDurationOn`）の問題ではなく
> segmentation モデルが話者交代を検出していないためで、`MinDurationOn` を 0.3 → 0.05 まで下げても
> **問題の箇所には 1 本も生成されない**（区間の総数は 38 → 51 本に増えるが、増えるのは別の時間帯である）。
> sherpa-onnx が配布する代替 segmentation モデル 2 種（reverb-diarization v1 / v2）でも同じ箇所を取り逃がす。
> **したがって「割り当て規則を変えれば直る」種類の誤りではない。** 選ぶ先の話者区間が存在しない。
> REQ-TRX-DIA-05 の規則を「重なりがあれば短いほうを採る」「僅差なら話者不明にする」等へ変えても、
> 実測では誤りは 1 件も減らず、正しく取れていた行を失うだけだった（T142 §11-3）。
>
> **時刻の精度（T142 / T143 の実測）:** Whisper のセグメントは時間枠を使い切ると残りのトークンを
> 末尾時刻へ押し込むため、発話時間帯が実際より手前で切れることがある（実測: 末尾トークンが
> 長さ 0 のセグメントは 58 件中 20 件）。話者境界が発話時間帯のすぐ外側 0.5 秒以内にある
> セグメントは 58 件中 28 件ある。
> **ただしこれを後処理で直すことはできない。** Whisper のセグメントは隙間なく連続して並ぶため、
> 「飽和した末尾を次のセグメントの開始まで伸ばす」には**余地が無い**（20 件中 17 件で 0 秒）。
> 余地を無視して 0.2〜1.0 秒伸ばすと、正しく取れていた区別を 1 つ失う。
> 発話時間帯に ±0.1〜0.5 秒の許容幅を持たせても、直る行は 0 件で誤りが 1 件増える（T143 §7-3）。
> **そもそも時刻ずれの主因は末尾の飽和ではない。** 切り出し位置を変えて 12 通りに
> デコードし直すと、問題の相槌は 12 回中 2 回しか現れず、残りでは前後の発話が同じ時間帯を
> 連続して埋める（T143 §7-2）。**Whisper は重なって発せられた発話を直列のセグメント列へ
> 置き直すため、その時刻は当てにできない。** 後処理では届かない種類の誤りである。

> **話者 ID の数（T138 の実測）:** 出力に現れる話者 ID の数は**実際の人数より多くなる**。
> 実測では、話者 2 名の対話（2 分 42 秒 / 58 行）で 6 種、15 分程度の会議で 36〜72 種だった。
> 同一人物が複数の ID に割れるためである（逆に別人が 1 つの ID にまとまることもある）。
> したがって**話者 ID を人物の一意な識別子として扱ってはならない。**
> 断片化の主因はクラスタリング閾値ではなく、**ごく短い発話区間の話者埋め込みが安定しないこと**である
> — 実測では話者 ID の 33〜56% が「合計 2 秒未満しか話さない ID」で、
> それらが占める発話時間は全体の 3〜5% にすぎない。埋め込みモデル 4 種 × 閾値 0.4〜0.7 の実測では、
> **既定（campplus zh_en / 0.5）以外はいずれも正しく取れていた区別を失った**ため、既定値は変えていない（T138 §7）。

> **なぜ短い区間だと割れるのか（T146 の実測）:** 話者埋め込みは、切り出しが短いほど
> **「同一人物を同一と認める力」を失う**。同一話者どうしのコサイン類似度は
> **0.25 秒で 0.31 / 0.5 秒で 0.35 / 1 秒で 0.51 / 3 秒で 0.76 / 10 秒で 0.90** と伸びるのに対し、
> 別話者どうしは長さによらず 0.22〜0.44 のままである。
> **既定のクラスタリング閾値 0.5 は、ちょうど「1 秒の区間の同一話者類似度」と同じ高さにある** —
> 実際の話者区間は中央値 1.03 秒・半数が 1 秒未満なので、**同一人物の区間の約半分が閾値を越えられない。**
> 0.25〜0.5 秒の区間では同一話者と別話者の分布がほぼ完全に重なるため、
> **閾値をどこに置いても正しく分けられない**（T146 §7-1）。
>
> **短い発話は話者区間そのものが作られない（T146 の実測）:** 正解が全区間で分かる合成音声で測ると、
> 交代の長さが中央値 1.2 秒の音声では**話者交代の 9% しか検出されず**、話者の当たり方は
> 当て推量と同じ（正答率 51.4%）だった。交代が中央値 7.0 秒なら 92.0%、20 秒なら 97.5% である。
> **短い相槌の話者を当てる方法は、埋め込み側にもクラスタリング側にも無い**（T146 §7-2）。
>
> **話者数を指定しても直らない（T147 の実測）:** `KnownSpeakerCount` に**正しい人数**を与えても、
> 基準音声では正解の分かる 4 箇所の区別が **4/4 → 1/4** に落ち（2〜6 のどの値でも同じ）、
> 正解が全区間で分かる合成音声では**話者が 1 つに潰れて正答率 92.0% → 52.0%**（＝当て推量）になった。
> ID の数だけは劇的に減る（会議で 108 種 → 4〜8 種）が、それは別人を飲み込んだ結果である（T147 §7）。

> **話者 ID の有効範囲:** 話者 ID は**その音声ファイルの中でのみ**有効である。
> 別のファイルの `話者1` が同一人物であることを意味しない。声紋の登録も個人の識別も行っていない。

## 非機能要件

| ID | 要件 | 補足 |
|---|---|---|
| NFR-01 | UI スレッド以外から UI プロパティを更新する場合は必ず `Dispatcher` 経由で行う | [CLAUDE.md](../../CLAUDE.md) の開発ルールに準拠。`MainViewModel` 内の各イベントハンドラで徹底 |
| NFR-02 | NAudio を直接利用し、独自の録音抽象化レイヤーを設けない | [CLAUDE.md](../../CLAUDE.md) のアーキテクチャ方針 |
| NFR-03 | 音声処理（キャプチャコールバック、MP3 書き込み、文字起こし）はいずれも専用スレッド／バックグラウンドタスクで実行し、UI の応答性を阻害しない | `AudioMixerWriter` スレッド、`WhisperTranscription` スレッド、`Task.Run` |
| NFR-04 | `AudioCaptureService` / `TranscriptionService` は `IDisposable` を実装し、確保したネイティブリソース（デバイスハンドル・エンコーダ・Whisper プロセッサ）を確実に解放する | 各サービスの `Dispose` |
| NFR-05 | デバイス権限不足やハードウェア非対応など回復可能なエラーは機能を縮退（ソフトミュートのみ等）させて処理を継続し、アプリを落とさない | `AudioCaptureService` の try/catch 群 |
| NFR-06 | 長時間録音時もメモリを蓄積しないよう、MP3 エンコード・書き込みはストリーミング方式とする | `LameMP3FileWriter` へのチャンク単位書き込み |
| NFR-07 | 話者ダイアライゼーションが**有効な場合に限り**、ファイル文字起こしはデコード済みの 16kHz モノラル PCM をファイル全体ぶんメモリに保持する（**約 230 MB/時間**）。sherpa-onnx の Diarization API が音声全体を 1 つの配列で要求するため回避できない。無効時は従来どおりストリーミング処理でありメモリは増えない | `TranscriptionService.DecodeToMono16k` |
| NFR-08 | `SpeakerDiarizationService` は `IDisposable` を実装し、sherpa-onnx のネイティブリソースを確実に解放する。ネイティブ API が NULL ハンドルを返しうる箇所では、ラッパーの検査に頼らず呼び出し側で事前条件を検証する（REQ-TRX-DIA-08） | `SpeakerDiarizationService.Dispose` |
