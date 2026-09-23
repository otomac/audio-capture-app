using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 利用者に見せる文字列に開発情報が混ざっていないことを固定する（NFR-10 /
/// <c>docs/harness/30-coding-standards.md</c> §9）。
/// </summary>
/// <remarks>
/// 要件 ID・タスク ID・ADR 番号・クラス名／メソッド名は、リポジトリを読める開発者にしか意味がない。
/// 画面に出すと「利用者には調べようのない記号」になる。レビューでは見落とすため、ここで機械的に止める。
/// <para>
/// 走査するのは <c>.xaml</c> の表示属性（<c>Text</c> / <c>Content</c> / <c>ToolTip</c>）と、
/// ViewModel / Service が組み立てる利用者向け文言である。前者はソースツリーのファイルを直接読む
/// （XAML はビルド後のアセンブリからは戻せないため。パスは <see cref="CallerFilePathAttribute"/> で得る）。
/// </para>
/// </remarks>
public class UiTextTests
{
    /// <summary>画面に出してはならない識別子（§9 の表）。</summary>
    private static readonly (string Name, Regex Pattern)[] ForbiddenPatterns =
    [
        ("要件 ID", new Regex(@"\b(REQ-[A-Z]+(-[A-Z]+)*-\d+|NFR-\d+)\b", RegexOptions.Compiled)),
        ("タスク ID", new Regex(@"\bT\d{3}\b", RegexOptions.Compiled)),
        ("ADR 番号", new Regex(@"\bADR-\d+\b", RegexOptions.Compiled)),
        ("クラス名・メソッド名", new Regex(
            @"\b(TranscriptionService|AudioCaptureService|SpeakerDiarizationService|SettingsService|MainViewModel|WhisperFactory|StopSession|StartSession|TranscribeFileAsync|IsNotBusy|Dispatcher)\b",
            RegexOptions.Compiled)),
    ];

    /// <summary>XAML の表示属性を拾う。バインディング（<c>{Binding …}</c>）は文言ではないので除く。</summary>
    private static readonly Regex DisplayAttribute =
        new(@"(?:Text|Content|ToolTip)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    private static string SourceRoot([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceRoot(), "AudioCaptureApp"), "*.xaml", SearchOption.AllDirectories))
        {
            if (!path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                data.Add(Path.GetRelativePath(SourceRoot(), path));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void XamlDisplayText_HasNoDeveloperIdentifiers(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(SourceRoot(), relativePath));

        foreach (Match attribute in DisplayAttribute.Matches(text))
        {
            var value = attribute.Groups["value"].Value;
            if (value.StartsWith('{'))
            {
                continue;   // {Binding …} / {StaticResource …} は文言ではない
            }

            AssertNoDeveloperIdentifier(value, relativePath);
        }
    }

    [Fact]
    public void XamlFiles_AreFound()
    {
        // ソースツリーを辿れていないと、上の Theory が 0 件で「緑」になってしまう
        Assert.True(XamlFiles().Count >= 5);
    }

    /// <summary>ViewModel / Service が組み立てる利用者向け文言。</summary>
    public static TheoryData<string> ViewModelTexts()
    {
        var data = new TheoryData<string>
        {
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.Available),
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.ModelMissing),
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.Disabled),
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.Available),
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.ModelMissing),
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.Disabled),
            MainViewModel.FileTranscriptionCancelNoticeFor(waitingForDiarization: true),
            MainViewModel.FileTranscriptionCancelNoticeFor(waitingForDiarization: false),
            MainViewModel.FileTranscriptionCloseConfirmation(isTranscribingFile: true)!,
            MainViewModel.CloseConfirmationMessage(isRecording: true, isStopping: false, isTranscribingFile: false)!,
            MainViewModel.CloseConfirmationMessage(isRecording: false, isStopping: true, isTranscribingFile: false)!,
            MainViewModel.CloseConfirmationMessage(isRecording: false, isStopping: false, isTranscribingFile: true)!,
            MainViewModel.StoppingStatusFor(113.8),
            MainViewModel.StoppingStatusFor(0.0),
            MainViewModel.AbortStopConfirmationMessage(113.8),
            MainViewModel.FileTranscriptionFailureMessageFor(null),
            MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.FileName),
            MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.CreationTime),
            MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.LastWriteMinusDuration),
            MainViewModel.ValidateWhisperModelEntry([], "", "", null, _ => true)!,
            MainViewModel.ValidateWhisperModelEntry([], "a", "x", null, _ => false)!,
            TranscriptionService.OpenFailureMessage(@"C:\a\b.m4a", "reason"),
            TranscriptionService.TranscribePhase,
            TranscriptionService.DiarizePhase,
            TranscriptionService.LoadModelPhase,
        };

        foreach (var option in SpeakerCountOptions.All)
        {
            data.Add(option.DisplayName);
        }

        foreach (var language in TranscriptionLanguages.ForFile)
        {
            data.Add(language.DisplayName);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ViewModelTexts))]
    public void ViewModelText_HasNoDeveloperIdentifiers(string text)
    {
        AssertNoDeveloperIdentifier(text, "ViewModel / Service の文言");
    }

    private static void AssertNoDeveloperIdentifier(string value, string where)
    {
        foreach (var (name, pattern) in ForbiddenPatterns)
        {
            var match = pattern.Match(value);
            Assert.False(
                match.Success,
                $"{where}: 利用者に見せる文字列に{name}「{match.Value}」が含まれています（NFR-10）。" +
                $"出典の記号ではなく事実そのものを書いてください。該当文字列: {value}");
        }
    }
}