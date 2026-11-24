namespace AudioCaptureApp.Services;

/// <summary>文字起こしの言語の選択肢 1 件（REQ-TRX-10）。</summary>
/// <param name="Code">Whisper へ渡す言語コード。<c>auto</c> は自動判定を表す。</param>
/// <param name="DisplayName">ドロップダウンに出す表示名。</param>
public sealed record TranscriptionLanguage(string Code, string DisplayName);

/// <summary>
/// 文字起こしの言語の一覧と正規化（REQ-TRX-10）。
/// </summary>
/// <remarks>
/// Whisper が扱う 99 言語をすべて並べることはしない。
/// **自動判定はファイル側にのみ置く** — ライブ経路は最大 6 秒（遅れているときは 20 秒）のチャンク（REQ-TRX-LIVE-10）で
/// 言語検出が誤りやすいと考えられるが、その誤検出率を実測していないためである。
/// 設定値は手編集され得るので、`SilenceCutOptions` などと同じく必ず正規化して使う。
/// </remarks>
public static class TranscriptionLanguages
{
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>自動判定。Whisper へは言語コードではなく検出の有効化として渡す。</summary>
    public const string Auto = "auto";

    /// <summary>ライブ文字起こしの選択肢（REQ-TRX-LIVE-14）。自動判定は含めない。</summary>
    public static IReadOnlyList<TranscriptionLanguage> ForLive { get; } =
    [
        new(Japanese, "日本語"),
        new(English, "英語")
    ];

    /// <summary>ファイル文字起こしの選択肢（REQ-TRX-FILE-16）。自動判定を含む。</summary>
    public static IReadOnlyList<TranscriptionLanguage> ForFile { get; } =
    [
        new(Japanese, "日本語"),
        new(English, "英語"),
        new(Auto, "自動判定")
    ];

    /// <summary>ライブ用に正規化する。未知のコードと <c>auto</c> は日本語へ倒す。</summary>
    public static string NormalizeForLive(string? code) => Normalize(code, ForLive);

    /// <summary>ファイル用に正規化する。未知のコードは日本語へ倒す。</summary>
    public static string NormalizeForFile(string? code) => Normalize(code, ForFile);

    private static string Normalize(string? code, IReadOnlyList<TranscriptionLanguage> allowed)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Japanese;
        }

        var trimmed = code.Trim();
        foreach (var language in allowed)
        {
            if (string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return language.Code;
            }
        }

        return Japanese;
    }
}