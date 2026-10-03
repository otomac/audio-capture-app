using System.Text.Json;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 録音のメタデータ（T169 / §16）。名前の整形・参加者の整形・JSON。
/// </summary>
public class RecordingMetadataFileTests
{
    // --- 会議名の整形とファイル名 (REQ-META-02) ---

    [Fact]
    public void SanitizeMeetingName_RemovesInvalidCharsAndTrims()
    {
        // 無効な文字（\ / : * ? " < > |）を除き、前後の空白を落とす
        Assert.Equal("定例MTG 92", RecordingMetadataFile.SanitizeMeetingName("  定例/MTG: 9*2?  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>:\"|?*")]
    public void SanitizeMeetingName_OnlyInvalidOrBlank_ReturnsEmpty(string? name)
    {
        Assert.Equal("", RecordingMetadataFile.SanitizeMeetingName(name));
    }

    [Fact]
    public void WithMeetingName_InsertsBeforeExtension()
    {
        Assert.Equal(
            @"C:\out\20260922_100000_定例.mp3",
            RecordingMetadataFile.WithMeetingName(@"C:\out\20260922_100000.mp3", "定例"));
    }

    [Fact]
    public void WithMeetingName_TranscriptTxt_KeepsDoubleExtension()
    {
        // .transcript.txt は先頭の . より前に挟む（REQ-TRX-FILE-05）
        Assert.Equal(
            @"C:\in\meeting_定例.transcript.txt",
            RecordingMetadataFile.WithMeetingName(@"C:\in\meeting.transcript.txt", "定例"));
    }

    [Fact]
    public void WithMeetingName_Empty_ReturnsOriginal()
    {
        Assert.Equal(@"C:\out\a.mp3", RecordingMetadataFile.WithMeetingName(@"C:\out\a.mp3", ""));
        Assert.Equal(@"C:\out\a.mp3", RecordingMetadataFile.WithMeetingName(@"C:\out\a.mp3", "***"));
    }

    [Fact]
    public void WithMeetingName_AlreadySuffixed_ReturnsOriginal()
    {
        // 録音時に改名された音声に、JSON から読み込んだ同じ会議名を二重に付けない（REQ-TRX-FILE-05 / 19）
        Assert.Equal(
            @"C:\out\20260922_100000_定例.transcript.txt",
            RecordingMetadataFile.WithMeetingName(@"C:\out\20260922_100000_定例.transcript.txt", "定例"));
        Assert.Equal(
            @"C:\out\20260922_100000_定例.mp3",
            RecordingMetadataFile.WithMeetingName(@"C:\out\20260922_100000_定例.mp3", "定/例"));
    }

    [Fact]
    public void BuildMetadataPath_ReplacesExtensionWithJson()
    {
        // 音声（改名後）と同名の .json（REQ-META-01）
        Assert.Equal(@"C:\out\20260922_100000_定例.json",
            RecordingMetadataFile.BuildMetadataPath(@"C:\out\20260922_100000_定例.mp3"));
        Assert.Equal(@"C:\in\meeting_定例.json",
            RecordingMetadataFile.BuildMetadataPath(@"C:\in\meeting_定例.transcript.txt"));
    }

    [Fact]
    public void BuildTranscriptPath_WithMeetingName()
    {
        Assert.Equal(@"C:\in\a_定例.transcript.txt", TranscriptionService.BuildTranscriptPath(@"C:\in\a.m4a", "定例"));
        Assert.Equal(@"C:\in\a.transcript.txt", TranscriptionService.BuildTranscriptPath(@"C:\in\a.m4a", null));
    }

    [Fact]
    public void BuildTranscriptPath_RenamedRecording_SharesMetadataPathWithAudio()
    {
        // 改名済みの録音を同じ会議名で文字起こしすると、JSON の書き出し先は読み込み元と同じになる（REQ-TRX-FILE-19）
        var audio = @"C:\out\20260922_100000_定例.mp3";
        var transcript = TranscriptionService.BuildTranscriptPath(audio, "定例");

        Assert.Equal(@"C:\out\20260922_100000_定例.transcript.txt", transcript);
        Assert.Equal(
            RecordingMetadataFile.BuildMetadataPath(audio), RecordingMetadataFile.BuildMetadataPath(transcript));
    }

    // --- 実施日時の既定 (REQ-REC-13) ---

    [Fact]
    public void HeldAtText_SameDay_OmitsEndDate()
    {
        var text = RecordingMetadataFile.HeldAtText(
            new DateTime(2026, 9, 22, 10, 0, 0), new DateTime(2026, 9, 22, 11, 5, 30));

        Assert.Equal("2026-09-22 10:00〜11:05", text);
    }

    [Fact]
    public void HeldAtText_CrossMidnight_IncludesEndDate()
    {
        var text = RecordingMetadataFile.HeldAtText(
            new DateTime(2026, 9, 22, 23, 30, 0), new DateTime(2026, 9, 23, 0, 15, 0));

        Assert.Equal("2026-09-22 23:30〜2026-09-23 00:15", text);
    }

    // --- 参加者 (REQ-META-03 / 04) ---

    [Fact]
    public void ParseParticipants_SplitsOnNewlineCommaAndJapaneseComma()
    {
        var parsed = RecordingMetadataFile.ParseParticipants("山田\r\n 佐藤 ,鈴木、田中;\n\n高橋 ");

        Assert.Equal(["山田", "佐藤", "鈴木", "田中", "高橋"], parsed);
        Assert.Empty(RecordingMetadataFile.ParseParticipants("  \n , "));
        Assert.Empty(RecordingMetadataFile.ParseParticipants(null));
    }

    [Fact]
    public void NormalizeParticipants_EmailsSortedByDomainWithSpecialLast()
    {
        // 「特定のドメイン」以外をドメイン名の昇順で先に、「特定のドメイン」を最後に。同じドメイン内は入力順
        var result = RecordingMetadataFile.NormalizeParticipants(
            ["z@ours.example", "b@beta.example", "a@Alpha.example", "y@OURS.example", "c@beta.example"],
            "ours.example");

        Assert.Equal(["a", "b", "c", "z", "y"], result);
    }

    [Fact]
    public void NormalizeParticipants_NonEmailsKeepOrderFirst()
    {
        var result = RecordingMetadataFile.NormalizeParticipants(
            ["田中", "b@beta.example", "山田", "a@alpha.example"], "");

        Assert.Equal(["田中", "山田", "a", "b"], result);
    }

    [Fact]
    public void NormalizeParticipants_NoSpecialDomain_SortsAllByDomain()
    {
        var result = RecordingMetadataFile.NormalizeParticipants(
            ["x@zeta.example", "y@alpha.example"], null);

        Assert.Equal(["y", "x"], result);
    }

    // --- JSON (REQ-META-01) ---

    [Fact]
    public void Write_ProducesJapaneseFieldNamesAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        try
        {
            var metadata = RecordingMetadataViewModel.BuildMetadata("定例", "2026-09-22 10:00〜11:00", "a@x.example\n山田", null);
            RecordingMetadataFile.Write(path, metadata);

            var json = File.ReadAllText(path);
            Assert.Contains("\"会議名\": \"定例\"", json, StringComparison.Ordinal);
            Assert.Contains("\"実施日時\": \"2026-09-22 10:00〜11:00\"", json, StringComparison.Ordinal);
            Assert.Contains("\"参加者\"", json, StringComparison.Ordinal);

            var back = JsonSerializer.Deserialize<RecordingMetadata>(json);
            Assert.NotNull(back);
            Assert.Equal("定例", back.MeetingName);
            Assert.Equal(["山田", "a"], back.Participants);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- 既存の JSON の読み込みと更新 (REQ-TRX-FILE-19) ---

    [Fact]
    public void TryRead_ExistingFile_ReturnsMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        try
        {
            RecordingMetadataFile.Write(path, RecordingMetadataViewModel.BuildMetadata("定例", "2026-09-22 10:00〜11:00", "山田\n佐藤", null));

            var read = RecordingMetadataFile.TryRead(path);

            Assert.NotNull(read);
            Assert.Equal("定例", read.MeetingName);
            Assert.Equal("2026-09-22 10:00〜11:00", read.HeldAt);
            Assert.Equal(["山田", "佐藤"], read.Participants);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_MissingOrInvalid_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        Assert.Null(RecordingMetadataFile.TryRead(path));

        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(RecordingMetadataFile.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_MissingFields_AreEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"会議名\": \"定例\" }");

            var read = RecordingMetadataFile.TryRead(path);

            Assert.NotNull(read);
            Assert.Equal("定例", read.MeetingName);
            Assert.Equal("", read.HeldAt);
            Assert.Empty(read.Participants);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FormatParticipants_OnePerLineAndRoundTripsThroughParse()
    {
        var text = RecordingMetadataFile.FormatParticipants(["山田", " ", "佐藤 ", null]);

        Assert.Equal($"山田{Environment.NewLine}佐藤", text);
        Assert.Equal(["山田", "佐藤"], RecordingMetadataFile.ParseParticipants(text));
    }

    [Fact]
    public void Update_ReplacesMetadataKeysAndKeepsOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"メモ\": \"手で書いた\", \"会議名\": \"旧\", \"参加者\": [\"古\"] }");

            RecordingMetadataFile.Update(path, RecordingMetadataViewModel.BuildMetadata("新", "2026-09-22 10:00〜11:00", "山田", null));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("手で書いた", root.GetProperty("メモ").GetString());
            Assert.Equal("新", root.GetProperty("会議名").GetString());
            Assert.Equal("2026-09-22 10:00〜11:00", root.GetProperty("実施日時").GetString());
            Assert.Equal("山田", Assert.Single(root.GetProperty("参加者").EnumerateArray()).GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Update_InvalidExisting_ThrowsAndLeavesFileUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acapp-meta-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "[1, 2]");

            Assert.ThrowsAny<JsonException>(
                () => RecordingMetadataFile.Update(path, RecordingMetadataViewModel.BuildMetadata("新", "", "", null)));
            Assert.Equal("[1, 2]", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsMetadataEmpty_AllBlank_IsTrue()
    {
        // ファイル文字起こしでは 3 項目とも空なら JSON を作らない（REQ-TRX-FILE-18）
        Assert.True(RecordingMetadataViewModel.IsMetadataEmpty("", " ", ""));
        Assert.False(RecordingMetadataViewModel.IsMetadataEmpty("", "", "山田"));
    }
}