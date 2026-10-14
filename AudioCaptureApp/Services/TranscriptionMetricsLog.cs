using System.Globalization;
using System.IO;
using System.Text;

namespace AudioCaptureApp.Services;

/// <summary>
/// ライブ文字起こしの計測ログ（REQ-CFG-12）。Whisper の呼び出し 1 回につき 1 行の TSV を書く。
/// </summary>
/// <remarks>
/// <para>
/// **文字起こしの本文は書かない。** 書くのは時刻・位置・所要時間などの数値だけである。
/// 実アプリで速報（ADR-0007）と確定の所要時間を確かめるための隠し設定で、既定では作られない。
/// </para>
/// <para>
/// 確定パス（<c>WhisperTranscription</c>）と速報（<c>ProvisionalTranscription</c>）の 2 本のスレッドから書くので、
/// 1 行ずつ錠を取る。書けなくなったら記録をやめ、理由を 1 度だけ知らせる（文字起こしは止めない）。
/// </para>
/// </remarks>
internal sealed class TranscriptionMetricsLog : IDisposable
{
    /// <summary>1 行目に書く列名。</summary>
    internal const string Header = "t_s\tpass\tsource\tstart_s\taudio_s\tcall_ms\tsegments\tbehind\tpending_s\tinterval_s";

    /// <summary>位置（サンプル数）を秒へ直すための標本化周波数。</summary>
    private const double SampleRate = 16000.0;

    private readonly Lock _lock = new();
    private readonly Action<string>? _onError;
    private StreamWriter? _writer;

    private TranscriptionMetricsLog(StreamWriter writer, Action<string>? onError)
    {
        _writer = writer;
        _onError = onError;
    }

    /// <summary>
    /// 録音開始時刻からログの置き場を決める（<c>%APPDATA%\AudioCaptureApp\logs\metrics_yyyyMMdd_HHmmss.tsv</c>）。
    /// </summary>
    /// <remarks>
    /// 録音フォルダに置かないのは、停止後の改名（REQ-META-02）が <c>.mp3</c> / <c>.txt</c> しか動かさず、
    /// ログだけが旧名で取り残されるためである。
    /// </remarks>
    internal static string PathFor(DateTime sessionStart)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp",
            "logs",
            $"metrics_{sessionStart.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.tsv");

    /// <summary>
    /// ログを開いてヘッダーを書く。開けなければ <paramref name="onError"/> で理由を知らせて <c>null</c> を返す。
    /// </summary>
    internal static TranscriptionMetricsLog? Open(string path, Action<string>? onError)
    {
        StreamWriter? writer = null;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            writer.WriteLine(Header);
            var log = new TranscriptionMetricsLog(writer, onError);
            writer = null;
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            onError?.Invoke(FailureMessage(ex.Message));
            return null;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>確定パスの呼び出し 1 回分。</summary>
    /// <param name="elapsed">セッション開始からの経過時間（呼び出しの終わり）。</param>
    /// <param name="source">音源。</param>
    /// <param name="startPosition">渡した区間の先頭の絶対位置（REQ-TRX-LIVE-18）。</param>
    /// <param name="samples">渡した区間の長さ（パディング前、サンプル数）。</param>
    /// <param name="callMs">呼び出しの所要時間（ミリ秒）。</param>
    /// <param name="segments">返ってきた行の数。</param>
    /// <param name="behind">遅れているか（REQ-TRX-LIVE-13 の判定）。</param>
    /// <param name="pendingSeconds">まだ処理していない音声の長さ（秒）。</param>
    internal void WriteFinal(
        TimeSpan elapsed, AudioSourceType source, long startPosition, int samples,
        double callMs, int segments, bool behind, double pendingSeconds)
        => WriteRow(elapsed, "final", source, startPosition, samples, callMs, segments,
            behind ? "1" : "0", pendingSeconds, "");

    /// <summary>速報の呼び出し 1 回分。</summary>
    /// <param name="intervalSeconds">この回のあとに決めた次の間隔（秒。REQ-TRX-LIVE-16）。</param>
    internal void WriteProvisional(
        TimeSpan elapsed, AudioSourceType source, long startPosition, int samples,
        double callMs, int segments, double pendingSeconds, double intervalSeconds)
        => WriteRow(elapsed, "provisional", source, startPosition, samples, callMs, segments,
            "", pendingSeconds, Format(intervalSeconds));

    private void WriteRow(
        TimeSpan elapsed, string pass, AudioSourceType source, long startPosition, int samples,
        double callMs, int segments, string behind, double pendingSeconds, string interval)
    {
        var line = string.Join('\t',
            Format(elapsed.TotalSeconds),
            pass,
            SourceName(source),
            Format(startPosition / SampleRate),
            Format(samples / SampleRate),
            Format(callMs),
            segments.ToString(CultureInfo.InvariantCulture),
            behind,
            Format(pendingSeconds),
            interval);

        string? failure = null;
        lock (_lock)
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 書けなくなったら以後は記録しない。理由は 1 度だけ知らせる
                failure = ex.Message;
                CloseWriter();
            }
        }

        if (failure != null)
        {
            _onError?.Invoke(FailureMessage(failure));
        }
    }

    private static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string SourceName(AudioSourceType source) => source switch
    {
        AudioSourceType.Mic => "mic",
        _ => "speaker"
    };

    private static string FailureMessage(string reason) => $"計測ログを書けないため記録をやめます: {reason}";

    /// <summary>錠の中で呼ぶ。</summary>
    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // 閉じるときの書き出し失敗は、すでに記録をやめているので無視する
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseWriter();
        }
    }
}