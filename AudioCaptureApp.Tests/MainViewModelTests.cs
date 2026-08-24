using System.Globalization;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

public class MainViewModelTests
{
    [Fact]
    public void PeakToDb_UnitPeak_ReturnsZeroDb()
    {
        var db = MainViewModel.PeakToDb(1.0f);

        Assert.Equal(0.0, db, precision: 1);
    }

    [Fact]
    public void PeakToDb_ZeroPeak_ReturnsMinDb()
    {
        var db = MainViewModel.PeakToDb(0.0f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_NegativePeak_ReturnsMinDb()
    {
        var db = MainViewModel.PeakToDb(-0.5f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_HalfPeak_ReturnsApproxMinus6Db()
    {
        var db = MainViewModel.PeakToDb(0.5f);

        // 20 * log10(0.5) ≈ -6.02
        Assert.Equal(-6.02, db, precision: 1);
    }

    [Fact]
    public void PeakToDb_OverUnitPeak_ClampsToMaxDb()
    {
        // 20 * log10(1.414) ≈ 3.01 → clamped to 3.0
        var db = MainViewModel.PeakToDb(1.414f);

        Assert.Equal(3.0, db);
    }

    [Fact]
    public void PeakToDb_VerySmallPeak_ClampsToMinDb()
    {
        // 20 * log10(0.000001) = -120 → clamped to -60
        var db = MainViewModel.PeakToDb(0.000001f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_TenthPeak_ReturnsMinus20Db()
    {
        var db = MainViewModel.PeakToDb(0.1f);

        // 20 * log10(0.1) = -20
        Assert.Equal(-20.0, db, precision: 1);
    }

    // --- 成果物フォルダを開く (T111) ---

    [Fact]
    public void BuildExplorerArguments_ExistingFile_SelectsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"acapp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var args = MainViewModel.BuildExplorerArguments(file);

            Assert.Equal($"/select,\"{file}\"", args);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildExplorerArguments_MissingFileButExistingFolder_OpensFolder()
    {
        // 成果物が削除されていても、置かれていたフォルダは開く（REQ-OPEN-03）
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var missing = Path.Combine(folder, $"acapp-missing-{Guid.NewGuid():N}.txt");

        var args = MainViewModel.BuildExplorerArguments(missing);

        Assert.Equal($"\"{folder}\"", args);
    }

    [Fact]
    public void BuildExplorerArguments_MissingFileAndFolder_ReturnsNull()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), $"acapp-nodir-{Guid.NewGuid():N}", "result.txt");

        Assert.Null(MainViewModel.BuildExplorerArguments(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildExplorerArguments_BlankPath_ReturnsNull(string path)
    {
        // 起動直後など成果物が未設定の状態（REQ-OPEN-04 と対になる防御）
        Assert.Null(MainViewModel.BuildExplorerArguments(path));
    }

    // --- 作業中は保存先を開けない (T121) ---

    [Fact]
    public void CanOpenResultFolder_IdleWithResult_IsTrue()
    {
        Assert.True(MainViewModel.CanOpenResultFolderFor(@"C:\out\20260818_120000.txt", isNotBusy: true));
    }

    [Fact]
    public void CanOpenResultFolder_Busy_IsFalse()
    {
        // 録音中・停止処理中・ファイル文字起こし中は、保持しているのが直前の成果物のため無効
        // （REQ-OPEN-05）
        Assert.False(MainViewModel.CanOpenResultFolderFor(@"C:\out\20260818_120000.txt", isNotBusy: false));
    }

    [Fact]
    public void CanOpenResultFolder_IdleWithoutResult_IsFalse()
    {
        // 起動直後（REQ-OPEN-04）
        Assert.False(MainViewModel.CanOpenResultFolderFor("", isNotBusy: true));
    }

    // --- ファイル文字起こしの開始時刻 (T113 / REQ-TRX-FILE-10) ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseStartTime_Blank_ReturnsZero(string text)
    {
        // 空欄は「未指定」。これまでどおりファイル先頭を 00:00:00 として出力する
        Assert.True(MainViewModel.TryParseStartTime(text, out var startTime));
        Assert.Equal(TimeSpan.Zero, startTime);
    }

    [Theory]
    [InlineData("9:05", 9, 5)]
    [InlineData("09:05", 9, 5)]
    [InlineData("0:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData(" 14:30 ", 14, 30)]
    public void TryParseStartTime_ValidForms_AreAccepted(string text, int hours, int minutes)
    {
        Assert.True(MainViewModel.TryParseStartTime(text, out var startTime));
        Assert.Equal(new TimeSpan(hours, minutes, 0), startTime);
    }

    [Theory]
    [InlineData("24:00")]   // 24 時は存在しない
    [InlineData("9:5")]     // 分は 2 桁
    [InlineData("12:60")]   // 分が範囲外
    [InlineData("12")]      // 区切りが無い
    [InlineData("12:34:56")]// 秒は受け付けない
    [InlineData("あ:い")]
    public void TryParseStartTime_InvalidForms_AreRejected(string text)
    {
        // 拒否できないと、不正な時刻のまま文字起こしが走り出す
        Assert.False(MainViewModel.TryParseStartTime(text, out _));
    }

    // --- ファイル文字起こしの進捗率 (T113 / REQ-TRX-FILE-06) ---

    [Fact]
    public void FileTranscriptionProgress_ZeroTotal_ReturnsZero()
    {
        // 総時間を取れないファイルでゼロ除算しない
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromSeconds(5), TimeSpan.Zero);

        Assert.Equal(0.0, value);
    }

    [Fact]
    public void FileTranscriptionProgress_HalfProcessed_ReturnsFifty()
    {
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.Equal(50.0, value);
    }

    [Fact]
    public void FileTranscriptionProgress_ProcessedExceedsTotal_ClampsTo100()
    {
        // 最終チャンクは総時間を跨ぐことがある（20 秒単位で切り出すため）
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(10));

        Assert.Equal(100.0, value);
    }

    // --- 文字起こし表示ウィンドウの行バッファ (T114 / REQ-LIVEVIEW-04) ---

    [Fact]
    public void AppendLiveTranscriptLine_AddsToEnd()
    {
        var lines = new List<string>();

        MainViewModel.AppendLiveTranscriptLine(lines, "1 行目", maxLines: 10);
        MainViewModel.AppendLiveTranscriptLine(lines, "2 行目", maxLines: 10);

        Assert.Equal(["1 行目", "2 行目"], lines);
    }

    [Fact]
    public void AppendLiveTranscriptLine_OverLimit_DropsOldest()
    {
        // 捨てるのは先頭（古い行）。末尾から捨てると最新の行が消えて用を成さない
        var lines = new List<string> { "古", "中", "新" };

        MainViewModel.AppendLiveTranscriptLine(lines, "最新", maxLines: 3);

        Assert.Equal(["中", "新", "最新"], lines);
    }

    [Fact]
    public void AppendLiveTranscriptLine_BeyondConfiguredMaximum_KeepsNewest100()
    {
        // 実際の上限値（REQ-LIVEVIEW-04）を使って、超過分が古い方から落ちることを固定する。
        // 定数を 1,000 に戻す／破棄の向きを逆にする、のどちらでもこのテストが落ちる。
        var lines = new List<string>();
        for (int i = 0; i < 150; i++)
        {
            MainViewModel.AppendLiveTranscriptLine(
                lines, $"行 {i}", MainViewModel.MaxLiveTranscriptLines);
        }

        Assert.Equal(100, lines.Count);
        Assert.Equal("行 50", lines[0]);
        Assert.Equal("行 149", lines[^1]);
    }

    [Fact]
    public void AppendLiveTranscriptLine_AtLimit_KeepsAll()
    {
        // ちょうど上限。境界で 1 行余計に捨てないことを固定する
        var lines = new List<string> { "1", "2" };

        MainViewModel.AppendLiveTranscriptLine(lines, "3", maxLines: 3);

        Assert.Equal(["1", "2", "3"], lines);
    }

    // --- まとめて届いた行の間引き (T135 / REQ-LIVEVIEW-09) ---

    [Fact]
    public void AppendLiveTranscriptLines_OverLimit_AddsOnlyTheNewest()
    {
        // 本タスクの核心。上限を超えるぶんは「追加してから捨てる」のではなく
        // 最初から追加しない。追加すると 1 行ごとにレイアウトが走るためである。
        var lines = new List<string>();
        var batch = Enumerable.Range(0, 250).Select(i => $"行 {i}").ToList();

        MainViewModel.AppendLiveTranscriptLines(lines, batch, maxLines: 100);

        Assert.Equal(100, lines.Count);
        Assert.Equal("行 150", lines[0]);
        Assert.Equal("行 249", lines[^1]);
    }

    [Fact]
    public void AppendLiveTranscriptLines_SameResultAsOneByOne()
    {
        // 間引いても最終状態が 1 行ずつ追加した場合と一致すること。
        // ここが崩れると「見えるものが変わる」最適化になってしまう。
        var oneByOne = new List<string> { "既存 1", "既存 2" };
        var batched = new List<string> { "既存 1", "既存 2" };
        var batch = Enumerable.Range(0, 250).Select(i => $"行 {i}").ToList();

        foreach (var line in batch)
        {
            MainViewModel.AppendLiveTranscriptLine(oneByOne, line, maxLines: 100);
        }

        MainViewModel.AppendLiveTranscriptLines(batched, batch, maxLines: 100);

        Assert.Equal(oneByOne, batched);
    }

    [Fact]
    public void AppendLiveTranscriptLines_UnderLimit_KeepsExistingAndTrims()
    {
        // 上限より短いバッチでは 1 行も捨てず、既存の行と合わせて上限で切ること
        var lines = new List<string> { "既存 1", "既存 2", "既存 3" };

        MainViewModel.AppendLiveTranscriptLines(lines, ["新 1", "新 2"], maxLines: 4);

        Assert.Equal(["既存 2", "既存 3", "新 1", "新 2"], lines);
    }

    [Fact]
    public void AppendLiveTranscriptLines_SingleLine_BehavesLikeAppendLiveTranscriptLine()
    {
        // ライブ文字起こしは 1 行ずつ届く。その経路の挙動が変わらないことを固定する
        var lines = new List<string> { "既存" };

        MainViewModel.AppendLiveTranscriptLines(lines, ["新"], maxLines: 10);

        Assert.Equal(["既存", "新"], lines);
    }

    [Fact]
    public void AppendLiveTranscriptLines_EmptyBatch_ChangesNothing()
    {
        // 引き取りが空振りすることがある（旗を取り出しより先に下ろすため）。
        // そのとき既存の表示を壊さないこと
        var lines = new List<string> { "既存 1", "既存 2" };

        MainViewModel.AppendLiveTranscriptLines(lines, [], maxLines: 10);

        Assert.Equal(["既存 1", "既存 2"], lines);
    }

    // --- 終了時の確認 (T149 / REQ-REC-11 / REQ-TRX-FILE-14) ---

    [Fact]
    public void CloseConfirmationMessage_Idle_ReturnsNull()
    {
        // 何も進行していなければ確認を挟まずに閉じる（従来どおりの挙動）
        var message = MainViewModel.CloseConfirmationMessage(
            isRecording: false, isStopping: false, isTranscribingFile: false);

        Assert.Null(message);
    }

    [Fact]
    public void CloseConfirmationMessage_Recording_AsksToStop()
    {
        var message = MainViewModel.CloseConfirmationMessage(
            isRecording: true, isStopping: false, isTranscribingFile: false);

        Assert.NotNull(message);
        Assert.Contains("録音中ですが終了しますか", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseConfirmationMessage_Stopping_AsksToWait()
    {
        // 停止処理中は「これから止める」のではなく「完了を待つ」ことを聞く
        var message = MainViewModel.CloseConfirmationMessage(
            isRecording: false, isStopping: true, isTranscribingFile: false);

        Assert.NotNull(message);
        Assert.Contains("完了を待って", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseConfirmationMessage_StoppingTakesPrecedenceOverRecording()
    {
        // StopRecordingAsync の実装上、停止処理中は IsRecording も true のままである。
        // 判定の順序を逆にすると、この状態で「録音を停止しますか」と聞いてしまう
        var message = MainViewModel.CloseConfirmationMessage(
            isRecording: true, isStopping: true, isTranscribingFile: false);

        Assert.NotNull(message);
        Assert.Contains("完了を待って", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseConfirmationMessage_TranscribingFile_AsksToCancel()
    {
        var message = MainViewModel.CloseConfirmationMessage(
            isRecording: false, isStopping: false, isTranscribingFile: true);

        Assert.NotNull(message);
        Assert.Contains("中止して終了しますか", message, StringComparison.Ordinal);
    }

    // --- 開始時刻の自動入力 (T150 / REQ-TRX-FILE-15) ---

    /// <summary>③に落ちないはずの場面で音声長が読まれたら分かるようにする。</summary>
    private static Func<TimeSpan?> NeverCalled()
        => () => throw new InvalidOperationException("音声長を読んではいけない場面で読まれた");

    [Fact]
    public void TryParseRecordedFileNameTime_RecordedName_Parses()
    {
        var ok = MainViewModel.TryParseRecordedFileNameTime("20260823_140530.mp3", out var startTime);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 8, 23, 14, 5, 30), startTime);
    }

    [Fact]
    public void TryParseRecordedFileNameTime_NameWithSuffix_Rejects()
    {
        // 名前全体が一致しない限り採らない。無関係な数字を時刻と誤読するより空欄が良い
        Assert.False(MainViewModel.TryParseRecordedFileNameTime("会議_20260823_140530.mp3", out _));
        Assert.False(MainViewModel.TryParseRecordedFileNameTime("20260823_140530_edited.mp3", out _));
    }

    [Fact]
    public void TryParseRecordedFileNameTime_InvalidDate_Rejects()
    {
        // 13 月・25 時は存在しない
        Assert.False(MainViewModel.TryParseRecordedFileNameTime("20261332_140530.mp3", out _));
        Assert.False(MainViewModel.TryParseRecordedFileNameTime("20260823_250000.mp3", out _));
    }

    [Fact]
    public void InferStartTime_RecordedFileName_UsesFileName()
    {
        // ①が取れたら②③は見ない（作成日時が食い違っていても①を優先する）
        var estimate = MainViewModel.InferStartTime(
            "20260823_140530.mp3",
            creationTime: new DateTime(2026, 8, 24, 9, 0, 0),
            lastWriteTime: new DateTime(2026, 8, 24, 10, 0, 0),
            NeverCalled());

        Assert.Equal("14:05", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.FileName, estimate.Source);
    }

    [Fact]
    public void InferStartTime_FullPath_UsesFileNamePart()
    {
        // 呼び出し側はフルパスを渡す（RequestFileTranscription）。パスが付いても①が効くこと
        var estimate = MainViewModel.InferStartTime(
            Path.Combine("C:", "rec", "20260823_140530.mp3"),
            creationTime: null,
            lastWriteTime: null,
            NeverCalled());

        Assert.Equal("14:05", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.FileName, estimate.Source);
    }

    [Fact]
    public void InferStartTime_PlainName_UsesCreationTime()
    {
        // ①が無く、作成日時 <= 更新日時（＝コピーの形跡が無い）なら②
        var estimate = MainViewModel.InferStartTime(
            "interview.wav",
            creationTime: new DateTime(2026, 8, 23, 9, 5, 0),
            lastWriteTime: new DateTime(2026, 8, 23, 10, 30, 0),
            NeverCalled());

        Assert.Equal("09:05", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.CreationTime, estimate.Source);
    }

    [Fact]
    public void InferStartTime_CopiedFile_UsesLastWriteMinusDuration()
    {
        // 作成日時 > 更新日時 は「コピーした日時」に書き換わった証拠。②を捨てて③へ落とす
        var estimate = MainViewModel.InferStartTime(
            "interview.wav",
            creationTime: new DateTime(2026, 8, 24, 18, 0, 0),
            lastWriteTime: new DateTime(2026, 8, 23, 10, 30, 0),
            () => TimeSpan.FromMinutes(90));

        Assert.Equal("09:00", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.LastWriteMinusDuration, estimate.Source);
    }

    [Fact]
    public void InferStartTime_CopiedFileWithoutDuration_ReturnsEmpty()
    {
        // ③の材料（音声長）も取れなければ空欄。エラーにはしない
        var estimate = MainViewModel.InferStartTime(
            "broken.mp3",
            creationTime: new DateTime(2026, 8, 24, 18, 0, 0),
            lastWriteTime: new DateTime(2026, 8, 23, 10, 30, 0),
            () => null);

        Assert.Equal("", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.None, estimate.Source);
    }

    [Fact]
    public void InferStartTime_LastWriteMinusDurationCrossesMidnight_WrapsToPreviousDay()
    {
        // 日付をまたいでも時刻だけを採る（00:30 の 2 時間前は前日の 22:30）
        var estimate = MainViewModel.InferStartTime(
            "night.wav",
            creationTime: new DateTime(2026, 8, 25, 12, 0, 0),
            lastWriteTime: new DateTime(2026, 8, 24, 0, 30, 0),
            () => TimeSpan.FromHours(2));

        Assert.Equal("22:30", estimate.Text);
    }

    [Fact]
    public void InferStartTime_NoFileTimes_ReturnsEmpty()
    {
        // 日時がまったく取れない（ファイルへアクセスできない）場合は空欄のまま
        var estimate = MainViewModel.InferStartTime(
            "interview.wav", creationTime: null, lastWriteTime: null, () => TimeSpan.FromMinutes(10));

        Assert.Equal("", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.None, estimate.Source);
    }

    [Fact]
    public void InferStartTime_NoCreationTime_FallsBackToLastWriteMinusDuration()
    {
        // 作成日時だけ取れない場合も③で救う
        var estimate = MainViewModel.InferStartTime(
            "interview.wav",
            creationTime: null,
            lastWriteTime: new DateTime(2026, 8, 23, 10, 30, 0),
            () => TimeSpan.FromMinutes(30));

        Assert.Equal("10:00", estimate.Text);
        Assert.Equal(MainViewModel.StartTimeSource.LastWriteMinusDuration, estimate.Source);
    }

    [Theory]
    [InlineData("20260823_140530.mp3", null, null)]
    [InlineData("interview.wav", "2026-08-23T09:05:00", "2026-08-23T10:30:00")]
    [InlineData("interview.wav", "2026-08-25T18:00:00", "2026-08-23T00:30:00")]
    public void InferStartTime_Result_IsAcceptedByTryParseStartTime(
        string fileName, string? creation, string? lastWrite)
    {
        // 自動入力した値は、必ず入力欄の書式（REQ-TRX-FILE-10）として受理されなければならない。
        // ここが崩れると「開始」が押せないダイアログが出る
        var estimate = MainViewModel.InferStartTime(
            fileName,
            creation == null ? null : DateTime.Parse(creation, CultureInfo.InvariantCulture),
            lastWrite == null ? null : DateTime.Parse(lastWrite, CultureInfo.InvariantCulture),
            () => TimeSpan.FromMinutes(90));

        Assert.True(MainViewModel.TryParseStartTime(estimate.Text, out _));
    }

    [Fact]
    public void StartTimeHintFor_None_IsEmpty()
    {
        // 推定していないときに注意書きだけ残らないこと
        Assert.Equal("", MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.None));
        Assert.NotEqual("", MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.FileName));
        Assert.NotEqual("", MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.CreationTime));
        Assert.NotEqual(
            "", MainViewModel.StartTimeHintFor(MainViewModel.StartTimeSource.LastWriteMinusDuration));
    }

    // --- ダイアログを閉じるときの確認 (T151 / REQ-TRX-FILE-13) ---

    [Fact]
    public void FileTranscriptionCloseConfirmation_Idle_ReturnsNull()
    {
        // 開始前・完了後は確認せずに閉じる（キャンセルボタン・自動クローズの経路）
        Assert.Null(MainViewModel.FileTranscriptionCloseConfirmation(isTranscribingFile: false));
    }

    [Fact]
    public void FileTranscriptionCloseConfirmation_Transcribing_AsksToCancel()
    {
        // 処理中に閉じる操作は中止と同義になる。Esc でも閉じうるため、黙って捨てない
        var message = MainViewModel.FileTranscriptionCloseConfirmation(isTranscribingFile: true);

        Assert.NotNull(message);
        Assert.Contains("中止して閉じますか", message, StringComparison.Ordinal);
    }

    // --- 話者識別の状態表示 (T152 / REQ-TRX-DIA-15) ---

    [Fact]
    public void DiarizationAvailabilityFor_Disabled_IsDisabled()
    {
        // 設定が無効なら、モデルが揃っていても「無効」
        Assert.Equal(
            MainViewModel.DiarizationAvailability.Disabled,
            MainViewModel.DiarizationAvailabilityFor(enabled: false, modelFilesExist: true));
    }

    [Fact]
    public void DiarizationAvailabilityFor_EnabledWithoutModels_IsModelMissing()
    {
        // 「有効にしたのに話者欄が出ない」の主因。無効と区別できることが要点
        Assert.Equal(
            MainViewModel.DiarizationAvailability.ModelMissing,
            MainViewModel.DiarizationAvailabilityFor(enabled: true, modelFilesExist: false));
    }

    [Fact]
    public void DiarizationAvailabilityFor_EnabledWithModels_IsAvailable()
    {
        Assert.Equal(
            MainViewModel.DiarizationAvailability.Available,
            MainViewModel.DiarizationAvailabilityFor(enabled: true, modelFilesExist: true));
    }

    [Fact]
    public void DiarizationStatusTextFor_AllStates_AreDistinctAndNonEmpty()
    {
        var texts = new[]
        {
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.Available),
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.ModelMissing),
            MainViewModel.DiarizationStatusTextFor(MainViewModel.DiarizationAvailability.Disabled)
        };

        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(3, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DiarizationTooltipFor_AllStates_AreDistinctAndNonEmpty()
    {
        var texts = new[]
        {
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.Available),
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.ModelMissing),
            MainViewModel.DiarizationTooltipFor(MainViewModel.DiarizationAvailability.Disabled)
        };

        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(3, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IsSpeakerDiarizationReadyFor_OnlyAvailable_IsTrue()
    {
        // チェックが入るのは実際に使える見込みのときだけ。モデル未配置で入れてはならない
        Assert.True(
            MainViewModel.IsSpeakerDiarizationReadyFor(MainViewModel.DiarizationAvailability.Available));
        Assert.False(
            MainViewModel.IsSpeakerDiarizationReadyFor(MainViewModel.DiarizationAvailability.ModelMissing));
        Assert.False(
            MainViewModel.IsSpeakerDiarizationReadyFor(MainViewModel.DiarizationAvailability.Disabled));
    }
}