using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace AudioCaptureApp.Models;

public class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioCapture");

    public string? LastSelectedDeviceId { get; set; }
    public string? LastSelectedLoopbackDeviceId { get; set; }

    public bool TranscriptionEnabled { get; set; }

    /// <summary>
    /// ライブ文字起こしに使う（選択中の）Whisper モデルの実パス（REQ-CFG-04）。
    /// 一覧（<see cref="WhisperModelList"/>）の導入後も、互換のためこのキーに実パスを書き続ける。
    /// 空なら「モデル未設定」。
    /// </summary>
    public string WhisperModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp", "models", "ggml-small.bin");

    /// <summary>
    /// 登録済み Whisper モデルの一覧（REQ-CFG-08）。エイリアス名とパスの組。
    /// 旧バージョンの設定には無いため、読み込み時に <see cref="WhisperModelPath"/> から 1 件へ移行する
    /// （移行は <c>MainViewModel.MigrateWhisperModelList</c>）。
    /// </summary>
    /// <remarks>
    /// 読み取り専用のコレクション（CA2227 / CA1002）。System.Text.Json は setter の無いコレクションを
    /// 既定では読み飛ばすため、既存インスタンスへ要素を足す <see cref="JsonObjectCreationHandling.Populate"/> を指定する。
    /// </remarks>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<WhisperModelEntry> WhisperModelList { get; } = [];

    /// <summary>
    /// ファイル文字起こしに使うモデルの名前（REQ-CFG-09 / REQ-TRX-FILE-17）。<see cref="WhisperModelList"/> の
    /// <c>ModelName</c>。<c>null</c>（既定）または一覧に無い名前ならライブ用と同じモデルを使う。
    /// </summary>
    public string? FileWhisperModelName { get; set; }

    public bool UseGpuForTranscription { get; set; } = true;

    /// <summary>
    /// ライブ文字起こしの言語（REQ-CFG-07 / REQ-TRX-LIVE-14）。既定は日本語。
    /// 未知の値は読み込み時に既定へ倒す（`TranscriptionLanguages.NormalizeForLive`）。
    /// **ライブでは自動判定 (`auto`) を選べない**ため、書かれていても日本語になる。
    /// </summary>
    public string LiveTranscriptionLanguage { get; set; } = "ja";

    /// <summary>
    /// ファイル文字起こしの言語（REQ-CFG-07 / REQ-TRX-FILE-16）。既定は日本語。
    /// こちらは自動判定 (`auto`) を選べる。ライブ用とは独立である。
    /// </summary>
    public string FileTranscriptionLanguage { get; set; } = "ja";

    // ---- 録音の自動開始（REQ-REC-12 / REQ-CFG-10）----

    /// <summary>マイクの音量で録音を自動で開始するか。既定は OFF。UI は設定ウィンドウのチェックボックス。</summary>
    public bool AutoStartRecordingEnabled { get; set; }

    /// <summary>自動開始の閾値（dB、−60〜0）。この値以上を「声がある」とみなす。UI からは変更できない。</summary>
    public double AutoStartThresholdDb { get; set; } = -30.0;

    /// <summary>閾値以上がこの秒数連続したら開始する（0.5〜60）。UI からは変更できない。</summary>
    public double AutoStartSustainSeconds { get; set; } = 3.0;

    /// <summary>録音が止まってからこの秒数は自動開始しない（0〜600）。UI からは変更できない。</summary>
    public double AutoStartCooldownSeconds { get; set; } = 10.0;

    /// <summary>
    /// 参加者（REQ-META-04）の並び替えで最後に回すメールドメイン（REQ-CFG-11）。既定は空＝無し。
    /// 特定の組織に紐づく値をソースに持ち込まないため、設定で与える。UI からは変更できない。
    /// </summary>
    public string ParticipantDomainSortedLast { get; set; } = "";

    /// <summary>有声とみなす 100ms 窓の RMS 下限（-40dB 相当）。UI からは変更できない。</summary>
    public double SilenceRmsThreshold { get; set; } = 0.01;

    /// <summary>これ未満の無音を挟む有声区間どうしは結合する（秒）。UI からは変更できない。</summary>
    public double SilenceMergeGapSeconds { get; set; } = 2.0;

    /// <summary>有声区間の前後に付ける余白（秒）。UI からは変更できない。</summary>
    public double VoicedPaddingSeconds { get; set; } = 0.2;

    // ---- 話者ダイアライゼーション（REQ-TRX-DIA-*、ADR-0003）----
    // いずれも UI からは変更できない。settings.json を直接編集して設定する。

    /// <summary>
    /// 話者ダイアライゼーションを行うか（REQ-TRX-DIA-03）。既定は無効。
    /// 有効にするにはモデル 2 種をローカルへ配置しておく必要がある（README 参照）。
    /// ファイル文字起こしにのみ効き、録音中のライブ文字起こしには影響しない。
    /// </summary>
    public bool SpeakerDiarizationEnabled { get; set; }

    /// <summary>話者区間検出（pyannote 系 segmentation）モデルのパス。</summary>
    public string SpeakerSegmentationModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp", "models", "diarization", "segmentation.onnx");

    /// <summary>話者埋め込み（speaker embedding）モデルのパス。</summary>
    public string SpeakerEmbeddingModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp", "models", "diarization", "embedding.onnx");

    /// <summary>
    /// 話者数が未知のときに使うクラスタリング閾値（REQ-TRX-DIA-07）。
    /// 小さくすると話者を細かく分け、大きくするとまとめる。
    /// </summary>
    public double SpeakerClusteringThreshold { get; set; } = 0.5;

    /// <summary>
    /// 話者数が分かっている場合に指定する（REQ-TRX-DIA-07）。
    /// null または 0 以下なら未指定とみなし <see cref="SpeakerClusteringThreshold"/> を使う。
    /// **既定で固定の話者数を入れてはならない。**
    /// </summary>
    public int? KnownSpeakerCount { get; set; }

    /// <summary>
    /// 話者ダイアライゼーションの推論スレッド数（REQ-TRX-DIA-14）。
    /// スレッド数を変えても**話者区間の結果は変わらない**（速度だけの設定である）。
    /// 上限を 4 に置くのは実測で 4 を超えても速くならず、8 を超えると遅くなるためである。
    /// </summary>
    public int SpeakerDiarizationThreads { get; set; } = Math.Min(4, Environment.ProcessorCount);
}