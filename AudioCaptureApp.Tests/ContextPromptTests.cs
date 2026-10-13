using AudioCaptureApp.Services;
using Confirmed = AudioCaptureApp.Services.TranscriptionService.ConfirmedText;

namespace AudioCaptureApp.Tests;

/// <summary>確定パスの文脈プロンプト（T188 / REQ-TRX-LIVE-19 / REQ-CFG-13）と行頭の付け札の除去（REQ-TRX-LIVE-20）。</summary>
public class ContextPromptTests
{
    private static Confirmed Line(double start, double end, string text)
        => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    // --- 用語集 (REQ-CFG-13) ---

    [Fact]
    public void BuildGlossaryPrompt_JoinsTermsWithReadingComma()
    {
        Assert.Equal("高岡、ChatGPT、ワークスロップ。", TranscriptionService.BuildGlossaryPrompt("高岡\r\n ChatGPT \n\n  \nワークスロップ\n"));
    }

    [Fact]
    public void BuildGlossaryPrompt_JoinsTermsWithinLimitByWord()
    {
        // 10 文字の語を 12 個 → 「、」込みで 11 語 = 120 文字（上限ちょうどは使う）、12 語目で超える
        var terms = Enumerable.Range(0, 12).Select(i => new string((char)('あ' + i), 10)).ToList();

        var prompt = TranscriptionService.BuildGlossaryPrompt(string.Join('\n', terms));

        Assert.Equal(string.Join('、', terms.Take(11)) + "。", prompt);
        Assert.Equal(TranscriptionService.GlossaryPromptMaxChars, prompt.Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n \r\n")]
    public void BuildGlossaryPrompt_EmptyOrBlank_ReturnsEmpty(string? glossary)
    {
        Assert.Equal("", TranscriptionService.BuildGlossaryPrompt(glossary));
    }

    // --- 直前の確定文 (REQ-TRX-LIVE-19) ---

    [Fact]
    public void BuildContextPrompt_GlossaryFirstThenTailOf100Chars()
    {
        // 同じ文字の長い並びは繰り返しとして除かれるので、10 文字の並びを 16 回つなげた 160 文字にする
        const string Unit = "あいうえおかきくけこ";
        var longText = string.Concat(Enumerable.Repeat(Unit, 16));

        var prompt = TranscriptionService.BuildContextPrompt(
            "高岡。", [Line(0, 10, longText)], TimeSpan.FromSeconds(11));

        Assert.Equal("高岡。" + string.Concat(Enumerable.Repeat(Unit, 10)), prompt);
    }

    [Fact]
    public void BuildContextPrompt_OrdersBothSourcesByEndTime()
    {
        // 確定パスはソースごとに処理するので、届く順は時刻順とは限らない
        var recent = new[]
        {
            Line(10, 12, "スピーカーの二。"),
            Line(0, 3, "マイクの一。"),
            Line(4, 6, "スピーカーの一。"),
        };

        var prompt = TranscriptionService.BuildContextPrompt("", recent, TimeSpan.FromSeconds(13));

        Assert.Equal("マイクの一。スピーカーの一。スピーカーの二。", prompt);
    }

    [Fact]
    public void BuildContextPrompt_IgnoresLinesEndingAfterRegionStart()
    {
        // スピーカーが 10〜20 秒を先に確定した。マイクの 10 秒からの区間に、同じ時間帯のその文を入れない（T195）
        var recent = new[]
        {
            Line(2, 8, "前の発言。"),
            Line(10.5, 19, "同じ時間帯にもう一方の音源が確定した文。"),
        };

        var prompt = TranscriptionService.BuildContextPrompt("", recent, TimeSpan.FromSeconds(10));

        Assert.Equal("前の発言。", prompt);
    }

    [Fact]
    public void BuildContextPrompt_LineEndingExactlyAtRegionStart_IsUsed()
    {
        // 話し続けて 10 秒で切ったチャンクの続き。前のチャンクの最後の行はちょうど区間の始まりで終わる
        var prompt = TranscriptionService.BuildContextPrompt("", [Line(0, 10, "続いている話。")], TimeSpan.FromSeconds(10));

        Assert.Equal("続いている話。", prompt);
    }

    [Fact]
    public void BuildContextPrompt_ResetsAfter30SecondsOfSilence()
    {
        var recent = new[] { Line(0, 5, "前の話題。"), Line(40, 45, "今の話題。") };

        var prompt = TranscriptionService.BuildContextPrompt("", recent, TimeSpan.FromSeconds(46));

        Assert.Equal("今の話題。", prompt);
    }

    [Fact]
    public void BuildContextPrompt_RegionLongAfterLastLine_UsesGlossaryOnly()
    {
        var recent = new[] { Line(0, 5, "前の話題。") };

        Assert.Equal("高岡。", TranscriptionService.BuildContextPrompt("高岡。", recent, TimeSpan.FromSeconds(35)));
        Assert.Null(TranscriptionService.BuildContextPrompt("", recent, TimeSpan.FromSeconds(35)));
    }

    [Fact]
    public void BuildContextPrompt_SkipsBrokenLines()
    {
        var recent = new[]
        {
            Line(0, 2, "本題に入ります。"),
            Line(2, 4, "ご視聴ありがとうございました"),
            Line(4, 6, "はいはいはいはい"),
            Line(6, 8, "よろしくお願いします。"),
        };

        var prompt = TranscriptionService.BuildContextPrompt("", recent, TimeSpan.FromSeconds(9));

        Assert.Equal("本題に入ります。よろしくお願いします。", prompt);
    }

    [Fact]
    public void BuildContextPrompt_NothingToSay_ReturnsNull()
    {
        Assert.Null(TranscriptionService.BuildContextPrompt("", [], TimeSpan.Zero));
    }

    // --- 行頭の付け札 (REQ-TRX-LIVE-20) ---

    [Theory]
    [InlineData("(山本)そういう動画を分析させます。", "そういう動画を分析させます。")]
    [InlineData("（大橋）1個よくない?", "1個よくない?")]
    [InlineData("(大橋) でも、面白いね。", "でも、面白いね。")]
    [InlineData("(山本)", "")]
    [InlineData("(笑)", "")]
    public void StripSpeakerTag_RemovesLeadingTag(string text, string expected)
    {
        Assert.Equal(expected, TranscriptionService.StripSpeakerTag(text));
    }

    [Theory]
    [InlineData("今日は(仮)の案で進めます。")]               // 途中の括弧は残す
    [InlineData("(これは十文字を超える長い括弧書きです)本文")] // 長い括弧書きは付け札ではない
    [InlineData("(山本 さん)本文")]                         // 空白を含むものは残す
    [InlineData("山本)本文")]                               // 閉じ括弧だけは残す
    public void StripSpeakerTag_KeepsOtherParentheses(string text)
    {
        Assert.Equal(text, TranscriptionService.StripSpeakerTag(text));
    }
}