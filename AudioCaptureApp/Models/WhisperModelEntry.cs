namespace AudioCaptureApp.Models;

/// <summary>
/// 登録済み Whisper モデル 1 件（REQ-CFG-08）。`settings.json` の `WhisperModelList` の要素。
/// </summary>
/// <remarks>
/// POCO であり、変更通知は持たない。名前を変えるときは新しいインスタンスへ差し替える（T162 D7）。
/// </remarks>
public class WhisperModelEntry
{
    /// <summary>ドロップダウンに出すエイリアス。一覧内で一意（<c>Ordinal</c>）。</summary>
    public string ModelName { get; set; } = "";

    /// <summary>GGML モデルファイルの実パス。一覧内で一意（<c>OrdinalIgnoreCase</c>）。</summary>
    public string ModelPath { get; set; } = "";
}