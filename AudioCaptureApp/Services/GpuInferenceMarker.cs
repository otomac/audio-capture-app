using System.Globalization;
using System.IO;

namespace AudioCaptureApp.Services;

/// <summary>
/// GPU で推論している最中であることを示す印のファイル（REQ-GPU-08）。GPU で推論を始める前に置き、正常に終えたら消す。
/// </summary>
/// <remarks>
/// <para>
/// GPU のドライバーが止まると PC 全体が固まり、OS が再起動することがある（T199）。アプリは例外も受け取れず、
/// 後始末もできない。そこで「推論の前に置き、正常に終えたら消す」印を残し、次の起動で残っていたら
/// 「前回は GPU で推論している最中に終わった」とみなす。アプリの異常終了・停電・強制終了でも残り、区別しない。
/// </para>
/// <para>
/// 書き込み・削除に失敗しても例外を外へ出さない。印は安全側の補助で、無くても録音と文字起こしは動く。
/// </para>
/// </remarks>
public sealed class GpuInferenceMarker
{
    private readonly string _path;

    public GpuInferenceMarker()
        : this(DefaultPath)
    {
    }

    internal GpuInferenceMarker(string path) => _path = path;

    /// <summary>印の置き場（<c>%APPDATA%\AudioCaptureApp\gpu-inference.marker</c>）。</summary>
    internal static string DefaultPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp",
            "gpu-inference.marker");

    /// <summary>
    /// 印を置く。中身は何の推論をいつ始めたかの 1 行で、調べるときの手がかりにすぎない（判定には使わない）。
    /// </summary>
    /// <param name="purpose">何の推論か（<c>live</c> / <c>file</c>）。</param>
    public void Set(string purpose)
    {
        try
        {
            var folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(
                _path, $"{purpose} {DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 置けなくても推論は続ける（次の起動で GPU を止められないだけ）
        }
    }

    /// <summary>印を消す（推論を正常に終えたとき）。無ければ何もしない。</summary>
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても続ける（次の起動で GPU が OFF に倒れるだけで、壊れはしない）
        }
    }

    /// <summary>
    /// 起動時に 1 度だけ呼ぶ。印が残っていれば消して <c>true</c>（前回は GPU で推論している最中に終わった）。
    /// </summary>
    public bool TakeLeftover()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        Clear();
        return true;
    }
}