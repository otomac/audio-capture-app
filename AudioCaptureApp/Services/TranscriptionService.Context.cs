using System.Text;
using System.Text.RegularExpressions;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ライブの確定パスの文脈プロンプト（REQ-TRX-LIVE-19 / REQ-CFG-13）と
// 行頭の付け札の除去（REQ-TRX-LIVE-20）を担当する部分（T188）。組み立て方は T182 の M6 で測ったものと同じ。
public partial class TranscriptionService
{
    /// <summary>直前の確定文 1 行（セッション開始からの経過時間と、付け札を落とした本文）。</summary>
    internal readonly record struct ConfirmedText(TimeSpan Start, TimeSpan End, string Text);

    /// <summary>用語集のプロンプトの上限（文字。REQ-CFG-13）。whisper.cpp はプロンプトが長すぎると先頭から切るため。</summary>
    internal const int GlossaryPromptMaxChars = 120;

    /// <summary>直前の確定文から使う末尾の長さ（文字。REQ-TRX-LIVE-19）。</summary>
    internal const int ContextTailChars = 100;

    /// <summary>この長さ以上の間が空いたら、それより前の確定文は使わない（REQ-TRX-LIVE-19）。</summary>
    internal static readonly TimeSpan ContextResetSilence = TimeSpan.FromSeconds(30);

    /// <summary>直前の確定文として持つ行数（末尾 100 文字に足りる）。</summary>
    private const int MaxRememberedLines = 50;

    /// <summary>
    /// 確定パスに文脈プロンプトを渡すか（REQ-CFG-12 の <c>LiveContextPromptEnabled</c>）。読まれるのは <see cref="StartSession"/> だけ。
    /// </summary>
    public bool ContextPromptEnabled { get; set; } = true;

    /// <summary>用語集（1 行 1 語。REQ-CFG-13）。読まれるのは <see cref="StartSession"/> だけ。</summary>
    public string TranscriptionGlossary { get; set; } = "";

    /// <summary>このセッションで文脈プロンプトを使うか（録音開始時に固定）。</summary>
    private bool _contextPromptActive;

    /// <summary>このセッションの用語集のプロンプト（録音開始時に作る）。</summary>
    private string _glossaryPrompt = "";

    /// <summary>このセッションのライブの言語（録音開始時に固定。プロンプト付きの processor を作るときに使う）。</summary>
    private string _sessionLanguage = TranscriptionLanguages.Japanese;

    /// <summary>直前の確定文（両音源）。確定パスのワーカーだけが触る。録音開始で空にする。</summary>
    private readonly List<ConfirmedText> _recentConfirmed = [];

    /// <summary>録音開始時（ワーカーを起こす前）に、このセッションの文脈を用意する。</summary>
    private void PrepareContext()
    {
        _recentConfirmed.Clear();
        _contextPromptActive = ContextPromptEnabled;
        _glossaryPrompt = BuildGlossaryPrompt(TranscriptionGlossary);
        _sessionLanguage = LiveLanguage;
    }

    /// <summary>この区間に渡すプロンプト。使わない・何も無いなら <c>null</c>。</summary>
    private string? ContextPromptFor(TimeSpan regionStart)
        => _contextPromptActive ? BuildContextPrompt(_glossaryPrompt, _recentConfirmed, regionStart) : null;

    /// <summary>確定した行を直前の確定文に足す（確定パスのワーカーから）。直近 <see cref="MaxRememberedLines"/> 行だけ持つ。</summary>
    private void RememberConfirmed(TimeSpan start, TimeSpan end, string text)
    {
        _recentConfirmed.Add(new ConfirmedText(start, end, text));
        if (_recentConfirmed.Count > MaxRememberedLines)
        {
            _recentConfirmed.RemoveRange(0, _recentConfirmed.Count - MaxRememberedLines);
        }
    }

    /// <summary>
    /// 用語集をプロンプトの頭に置く形にする（REQ-CFG-13）。1 行 1 語。空白だけの行は捨て、「、」で連結し、
    /// <see cref="GlossaryPromptMaxChars"/> 文字を超える語から先は使わない（語の単位）。1 語以上あれば末尾に「。」を付ける。
    /// </summary>
    internal static string BuildGlossaryPrompt(string? glossary)
    {
        if (string.IsNullOrWhiteSpace(glossary))
        {
            return "";
        }

        var joined = new StringBuilder();
        foreach (var line in glossary.Split('\n'))
        {
            var term = line.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            int next = joined.Length + (joined.Length == 0 ? 0 : 1) + term.Length;
            if (next > GlossaryPromptMaxChars)
            {
                break;
            }

            if (joined.Length > 0)
            {
                joined.Append('、');
            }
            joined.Append(term);
        }

        return joined.Length == 0 ? "" : joined.Append('。').ToString();
    }

    /// <summary>
    /// 確定パスの文脈プロンプト（REQ-TRX-LIVE-19）。用語集（先頭）＋両音源の直前の確定文の末尾 <see cref="ContextTailChars"/> 文字。
    /// どちらも空なら <c>null</c>（プロンプトを渡さない）。
    /// </summary>
    /// <param name="glossaryPrompt"><see cref="BuildGlossaryPrompt"/> の結果。</param>
    /// <param name="recent">直前の確定文（両音源。並びは問わない）。</param>
    /// <param name="regionStart">これから文字起こしする区間の始まり（セッション開始からの経過時間）。</param>
    /// <remarks>
    /// <b>今の区間の始まりより前に終わった行だけ</b>を使う（T195）。確定パスは音源ごとに順に処理するので、
    /// 後から処理する音源のプロンプトに、もう一方の音源が同じ時間帯を確定した文が入りうる。2 音源が同じ音声を拾うと
    /// （イヤホン無しの会議）、Whisper がその文に引きずられて失敗し、確定パスの費用がほぼ倍になって行も欠けた。
    /// <para>
    /// 確定行を終わりの時刻順に並べ、間が <see cref="ContextResetSilence"/> 以上空いた所より後ろだけを使う。
    /// 今の区間の始まりが最後の行から同じだけ離れていれば使わない。壊れた行（空・定型の幻聴・繰り返し）は除く。
    /// </para>
    /// </remarks>
    internal static string? BuildContextPrompt(string glossaryPrompt, IReadOnlyList<ConfirmedText> recent, TimeSpan regionStart)
    {
        var lines = recent.Where(l => l.End <= regionStart).OrderBy(l => l.End).ToList();
        int from = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Start - lines[i - 1].End >= ContextResetSilence)
            {
                from = i;
            }
        }

        if (lines.Count > 0 && regionStart - lines[^1].End >= ContextResetSilence)
        {
            from = lines.Count;
        }

        var previous = new StringBuilder();
        for (int i = from; i < lines.Count; i++)
        {
            if (SanitizeProvisionalText(lines[i].Text) != null)
            {
                previous.Append(lines[i].Text);
            }
        }

        var tail = previous.Length > ContextTailChars
            ? previous.ToString(previous.Length - ContextTailChars, ContextTailChars)
            : previous.ToString();
        var prompt = glossaryPrompt + tail;
        return prompt.Length == 0 ? null : prompt;
    }

    /// <summary>行頭の付け札（空白を含まない 1〜10 文字の丸括弧書き）と、その後ろの空白。</summary>
    private static readonly Regex SpeakerTagPattern =
        new(@"^[（(][^（）()\s]{1,10}[)）]\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 確定行の行頭の付け札（<c>(山本)</c> / <c>（大橋）</c>）を落とす（REQ-TRX-LIVE-20）。付け札だけの行は空文字列になる。
    /// </summary>
    /// <remarks>
    /// Whisper は行頭に話者名のような付け札を幻聴し、文脈プロンプトを渡すと名前が用語集の人名に置き換わる（T182 の M6）。
    /// 存在しない話者名を確定文に残すのは誤った情報になるため落とす。行の途中の括弧書きは触らない。
    /// </remarks>
    internal static string StripSpeakerTag(string text) => SpeakerTagPattern.Replace(text, "", 1).Trim();
}