using System.IO;

namespace AudioCaptureApp.Models;

public class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioCapture");

    public string? LastSelectedDeviceId { get; set; }
    public string? LastSelectedLoopbackDeviceId { get; set; }

    public bool TranscriptionEnabled { get; set; }
    public string WhisperModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp", "models", "ggml-small.bin");

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