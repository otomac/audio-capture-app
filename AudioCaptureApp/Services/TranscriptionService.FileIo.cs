using System.Globalization;
using System.IO;
using NAudio.Wave;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ファイル文字起こしの入出力ファイルまわりを担当する部分（T193 で切り出した）。
// 音声ファイルを開く・出力名（REQ-TRX-FILE-05）・日時と長さ（REQ-TRX-FILE-15）・作りかけの出力の削除（REQ-TRX-FILE-07）。
public partial class TranscriptionService
{
    /// <summary>
    /// 音声ファイルを開く（REQ-TRX-FILE-03）。3 か所のデコード経路はすべてここを通る。
    /// </summary>
    /// <remarks>
    /// <see cref="AudioFileReader"/> は `.wav` 以外を Media Foundation（OS 標準デコーダー）へ委ねる。
    /// Windows の N エディションでは AAC デコーダーが無く `.m4a` を開けないため、
    /// 失敗の原因が環境かファイルかを切り分けられるよう、**メッセージに形式（拡張子）を含める**（T161）。
    /// Media Foundation は形式ごとに異なる COM 例外を投げ、型を列挙できないため全例外を包む。
    /// </remarks>
    /// <exception cref="InvalidOperationException">開けなかった。原因は内部例外。</exception>
    internal static AudioFileReader OpenAudioFile(string audioFilePath)
    {
        try
        {
            return new AudioFileReader(audioFilePath);
        }
        // CA1031: 対象は利用者が選んだ任意のファイルで、NAudio / Media Foundation は形式ごとに
        //         異なる例外（COMException・FileNotFoundException・ArgumentException 等）を投げる。
        //         どれであっても「開けなかった」として形式名付きのメッセージに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            throw new InvalidOperationException(OpenFailureMessage(audioFilePath, ex.Message), ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>「音声ファイルを開けなかった」ときのメッセージ。形式（拡張子）を必ず含める。</summary>
    internal static string OpenFailureMessage(string audioFilePath, string reason)
    {
        var ext = Path.GetExtension(audioFilePath);
        var format = string.IsNullOrEmpty(ext) ? "拡張子なし" : ext;
        var hint = string.Equals(format, ".m4a", StringComparison.OrdinalIgnoreCase)
            ? " Windows の N エディションでは Media Feature Pack が必要です。"
            : "";
        return $"音声ファイルを開けませんでした (形式: {format}): {reason}{hint}";
    }

    // {入力ファイル名}.transcript.txt を同じフォルダに配置
    // 例: audio.mp3 → audio.transcript.txt
    // （録音時に生成される audio.txt と名前衝突しないように）
    internal static string BuildTranscriptPath(string audioFilePath)
    {
        return Path.ChangeExtension(audioFilePath, ".transcript.txt");
    }

    /// <summary>
    /// 会議名付きの出力パス（REQ-TRX-FILE-05 / REQ-META-02）。`audio.mp3` + `定例` → `audio_定例.transcript.txt`。
    /// 会議名が空（または整形して空）なら会議名なしと同じ。
    /// </summary>
    internal static string BuildTranscriptPath(string audioFilePath, string? meetingName)
        => RecordingMetadataFile.WithMeetingName(BuildTranscriptPath(audioFilePath), meetingName);

    /// <summary>
    /// 既存のファイルを上書きしない出力パス（REQ-TRX-FILE-05 / T208）。
    /// <see cref="BuildTranscriptPath(string, string?)"/> の名前が空いていればそれを、
    /// 埋まっていれば `a.transcript (2).txt`、`(3)`… のうち空いている最小の番号を返す。
    /// 番号を `.transcript` と `.txt` の間に入れるのは、stem を変えずにメタデータ JSON を同じものに保つため（REQ-META-01）。
    /// </summary>
    /// <param name="exists">パスが既に使われているか（本番は <see cref="File.Exists(string?)"/>）。</param>
    internal static string BuildAvailableTranscriptPath(string audioFilePath, string? meetingName, Func<string, bool> exists)
    {
        var path = BuildTranscriptPath(audioFilePath, meetingName);
        if (!exists(path))
        {
            return path;
        }

        var withoutTxt = Path.ChangeExtension(path, null);   // a.transcript.txt → a.transcript
        for (var number = 2; ; number++)
        {
            var candidate = $"{withoutTxt} ({number.ToString(CultureInfo.InvariantCulture)}).txt";
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 音声ファイルの作成日時・最終更新日時を読む（REQ-TRX-FILE-15 の②③）。
    /// </summary>
    /// <returns>読めたら <c>true</c>。ファイルが無い・アクセスできない場合は <c>false</c>。</returns>
    internal static bool TryGetAudioFileTimes(
        string audioFilePath, out DateTime creationTime, out DateTime lastWriteTime)
    {
        creationTime = default;
        lastWriteTime = default;
        try
        {
            var info = new FileInfo(audioFilePath);
            if (!info.Exists)
            {
                return false;
            }
            creationTime = info.CreationTime;
            lastWriteTime = info.LastWriteTime;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 音声ファイルの長さ（全長）を読む。開始時刻の推定（REQ-TRX-FILE-15 の③）で使う。
    /// </summary>
    /// <returns>読めたら <c>true</c>。開けない・壊れている場合は <c>false</c>（例外は投げない）。</returns>
    /// <remarks>
    /// **呼ぶのは③に落ちたときだけにすること。** <see cref="AudioFileReader"/> は MP3 の
    /// フレーム表を作るためにファイル全体を走査するため、長いファイルでは無視できない時間がかかる。
    /// 返すのは**無音カット前のファイル全長**である（録音終了時刻からの逆算に使うため）。
    /// </remarks>
    internal static bool TryGetAudioDuration(string audioFilePath, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            using var reader = OpenAudioFile(audioFilePath);
            duration = reader.TotalTime;
            return duration > TimeSpan.Zero;
        }
        // CA1031: 対象は利用者が選んだ任意のファイルで、NAudio は形式ごとに異なる例外を投げる。
        //         ここは「推定できたら入れる」だけの補助機能なので、失敗は静かに諦める
        //         （REQ-TRX-FILE-15: 推定できなくてもエラーにしない）。
#pragma warning disable CA1031
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// 中止時に、この実行が作りかけた出力ファイルだけを消す（REQ-TRX-FILE-07）。
    /// </summary>
    /// <remarks>
    /// 従来経路（<paramref name="diarization"/> が <c>null</c>）は開始時点で出力ファイルを
    /// <c>append: false</c> で開いて中身を捨てているため、中止したら消すのが正しい。
    /// Diarization 経路はマージが終わるまで出力ファイルを開かない。中止時点では未作成なので、
    /// ここで無条件に消すと **前回成功したときの `.transcript.txt` を巻き添えで削除してしまう**。
    /// 消してよいのは「この実行が作ったもの」だけである。
    /// </remarks>
    private static void DeletePartialOutput(string path, SpeakerDiarizationService? diarization)
    {
        if (diarization != null)
        {
            return;
        }

        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除失敗は無視（ロック中など）
        }
    }
}