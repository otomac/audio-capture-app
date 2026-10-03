using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

/// <summary>
/// 録音停止後のメタデータ入力ダイアログ（<c>RecordingMetadataWindow</c>）の ViewModel（REQ-REC-13 / §16、ADR-0008）。
/// 会議名・実施日時・参加者の入力と、JSON の書き出し・改名の指示を担当する。
/// 生成と保持は <see cref="MainViewModel"/> が行い、<see cref="MainViewModel.RecordingMetadata"/> で公開する。
/// </summary>
/// <remarks>
/// 流れ: 親が停止処理の完了後に <see cref="Prepare"/> で既定値を入れて表示を要求し、
/// <c>MainWindow</c> が同期的にダイアログを出して、閉じたら <see cref="Complete"/> を呼ぶ。
/// 親の状態のうち書くのは <see cref="MainViewModel.LastResultPath"/> と <see cref="MainViewModel.StatusMessage"/> だけである。
/// </remarks>
public sealed partial class RecordingMetadataViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    internal RecordingMetadataViewModel(MainViewModel main)
    {
        _main = main;
    }

    /// <summary>会議名（REQ-META-02 でファイル名にも付く）。</summary>
    [ObservableProperty]
    private string _metadataMeetingName = "";

    /// <summary>実施日時（自由記述。既定は録音開始〜終了）。</summary>
    [ObservableProperty]
    private string _metadataHeldAt = "";

    /// <summary>参加者（複数行。改行・`,`・`、`・`;` 区切り。REQ-META-03）。</summary>
    [ObservableProperty]
    private string _metadataParticipantsText = "";

    /// <summary>ダイアログに出す対象（録音ファイル名）。</summary>
    [ObservableProperty]
    private string _metadataTargetName = "";

    /// <summary>
    /// メタデータ 3 項目から <see cref="RecordingMetadata"/> を組み立てる（REQ-META-01 / 03 / 04）。
    /// 会議名はファイル名用ではなく入力どおり（前後の空白だけ落とす）を JSON に書く。
    /// ファイル文字起こしのダイアログ（<see cref="FileTranscriptionViewModel"/>）も同じ規則で組み立てる。
    /// </summary>
    internal static RecordingMetadata BuildMetadata(
        string meetingName, string heldAt, string participantsText, string? domainSortedLast)
    {
        var metadata = new RecordingMetadata
        {
            MeetingName = (meetingName ?? "").Trim(),
            HeldAt = (heldAt ?? "").Trim()
        };
        foreach (var participant in RecordingMetadataFile.NormalizeParticipants(
                     RecordingMetadataFile.ParseParticipants(participantsText), domainSortedLast))
        {
            metadata.Participants.Add(participant);
        }

        return metadata;
    }

    /// <summary>3 項目とも空か（ファイル文字起こしで JSON を作らない条件。REQ-TRX-FILE-18）。</summary>
    internal static bool IsMetadataEmpty(string meetingName, string heldAt, string participantsText)
        => string.IsNullOrWhiteSpace(meetingName)
           && string.IsNullOrWhiteSpace(heldAt)
           && string.IsNullOrWhiteSpace(participantsText);

    /// <summary>
    /// 停止処理の完了後、入力の既定値を入れる（REQ-REC-13）。表示の要求は親が続けて上げる。
    /// </summary>
    internal void Prepare(RecordingSession session)
    {
        MetadataMeetingName = "";
        MetadataHeldAt = RecordingMetadataFile.HeldAtText(session.StartedAt, session.StoppedAt ?? DateTime.Now);
        MetadataParticipantsText = "";
        MetadataTargetName = System.IO.Path.GetFileName(session.FilePath);
    }

    /// <summary>
    /// ダイアログが閉じた（REQ-REC-13）。<paramref name="accepted"/> が「OK」なら JSON を書き、
    /// 会議名があれば `.mp3` / `.txt` を改名して <see cref="MainViewModel.LastResultPath"/> を追従させる（REQ-META-02）。
    /// 「キャンセル」なら何も残さない。
    /// </summary>
    public void Complete(bool accepted)
    {
        var session = _main.AudioCaptureService.CurrentSession;
        if (!accepted || session == null)
        {
            return;
        }

        // 改名先が既にある・失敗 → 元の名前のまま JSON だけ書く（REQ-META-02）
        var renameError = _main.AudioCaptureService.RenameSessionFiles(MetadataMeetingName);
        var mp3Path = session.FilePath;
        var txtPath = System.IO.Path.ChangeExtension(mp3Path, ".txt");
        var jsonPath = RecordingMetadataFile.BuildMetadataPath(mp3Path);

        try
        {
            RecordingMetadataFile.Write(
                jsonPath,
                BuildMetadata(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText,
                    _main.AppSettings.ParticipantDomainSortedLast));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _main.StatusMessage = $"メタデータの保存に失敗しました: {ex.Message}";
            return;
        }

        // REQ-OPEN-01: 成果物のパスを改名後へ追従させる
        var hasTxt = System.IO.File.Exists(txtPath);
        _main.LastResultPath = hasTxt ? txtPath : mp3Path;
        _main.StatusMessage = renameError == null
            ? $"保存完了: {mp3Path} (メタデータ: {System.IO.Path.GetFileName(jsonPath)})"
            : $"{renameError} メタデータは {System.IO.Path.GetFileName(jsonPath)} に保存しました";
    }
}