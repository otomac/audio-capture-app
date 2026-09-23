using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Services;

/// <summary>
/// 録音のメタデータ（§16）の名前の整形・参加者の整形・JSON の書き出し。
/// 純粋関数の集まりで、書き出し以外は I/O を持たない（テスト対象）。
/// </summary>
internal static class RecordingMetadataFile
{
    /// <summary>「〜」で結ぶ実施日時の既定書式（REQ-REC-13）。</summary>
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    // 日本語をエスケープしない（`会議名` がそのまま読めるように）。ファイルは自分しか読まないので安全。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 会議名をファイル名に使える形にする（REQ-META-02）。無効な文字を除き、前後の空白を落とす。
    /// 残らなければ空文字列（＝ファイル名に付けない）。
    /// </summary>
    public static string SanitizeMeetingName(string? meetingName)
    {
        if (string.IsNullOrWhiteSpace(meetingName))
        {
            return "";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(meetingName.Length);
        foreach (var ch in meetingName)
        {
            if (Array.IndexOf(invalid, ch) < 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// パスの拡張子の前に `_会議名` を挟む（REQ-META-02）。`.transcript.txt` のような二重拡張子は
    /// 先頭の `.` より前に挟む（`a.transcript.txt` → `a_会議名.transcript.txt`）。
    /// 会議名（整形後）が空ならそのまま返す。
    /// </summary>
    public static string WithMeetingName(string path, string? meetingName)
    {
        var name = SanitizeMeetingName(meetingName);
        if (name.Length == 0)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? fileName : fileName[..dot];
        var extensions = dot < 0 ? "" : fileName[dot..];
        return Path.Combine(directory, $"{stem}_{name}{extensions}");
    }

    /// <summary>
    /// メタデータ JSON のパス（REQ-META-01）。音声または `.transcript.txt` と同じ stem の `.json`。
    /// `a.mp3` → `a.json`、`a.transcript.txt` → `a.json`。
    /// </summary>
    public static string BuildMetadataPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? fileName : fileName[..dot];
        return Path.Combine(directory, stem + ".json");
    }

    /// <summary>実施日時の既定文字列（REQ-REC-13）。日をまたいだら終了側にも日付を付ける。</summary>
    public static string HeldAtText(DateTime startedAt, DateTime endedAt)
    {
        var start = startedAt.ToString(DateFormat, CultureInfo.InvariantCulture);
        var end = startedAt.Date == endedAt.Date
            ? endedAt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : endedAt.ToString(DateFormat, CultureInfo.InvariantCulture);
        return $"{start}〜{end}";
    }

    private static readonly char[] ParticipantSeparators = ['\r', '\n', ',', '、', ';'];

    /// <summary>参加者欄を分割する（REQ-META-03）。改行・`,`・`、`・`;` で区切り、空を捨てて前後の空白を落とす。</summary>
    public static List<string> ParseParticipants(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var part in text.Split(ParticipantSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>
    /// 参加者の整形（REQ-META-04）。メールの形（`@` を含む）は `@` より前を名前にし、ドメインで並べる —
    /// <paramref name="domainSortedLast"/> 以外をドメイン名の昇順で先に、<paramref name="domainSortedLast"/> を最後に。
    /// 同じドメイン内は入力順。メールの形でないものは入力順のまま先頭に置く。
    /// </summary>
    /// <param name="participants">入力順の参加者。</param>
    /// <param name="domainSortedLast">最後に回すドメイン（設定 `ParticipantDomainSortedLast`）。空なら無し。</param>
    public static List<string> NormalizeParticipants(IEnumerable<string> participants, string? domainSortedLast)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var plain = new List<string>();
        var emails = new List<(string Name, string Domain, int Order)>();
        int order = 0;
        foreach (var participant in participants)
        {
            var at = participant.IndexOf('@', StringComparison.Ordinal);
            if (at <= 0 || at == participant.Length - 1)
            {
                plain.Add(participant);
            }
            else
            {
                emails.Add((participant[..at], participant[(at + 1)..], order));
            }
            order++;
        }

        var last = (domainSortedLast ?? "").Trim();
        var sorted = emails
            .OrderBy(e => last.Length > 0 && string.Equals(e.Domain, last, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(e => e.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Order)
            .Select(e => e.Name);

        plain.AddRange(sorted);
        return plain;
    }

    /// <summary>メタデータを JSON として書き出す（REQ-META-01）。既存の同名ファイルは上書きする。</summary>
    public static void Write(string path, RecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false));
    }
}