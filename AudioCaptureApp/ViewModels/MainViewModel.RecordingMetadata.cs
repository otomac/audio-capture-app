using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、録音停止時のメタデータ入力（会議名・実施日時・参加者）と JSON の書き出し・改名の指示を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D / ADR-0006）。
public partial class MainViewModel
{
    // --- 録音のメタデータ (T169 / REQ-REC-13 / §16) ---

    /// <summary>
    /// メタデータ入力ダイアログを開いてほしい、という要求（REQ-REC-13）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド（ADR-0002 の規則 2・3）。
    /// **同期的に `ShowDialog` し、閉じたら <see cref="CompleteRecordingMetadata"/> を呼ぶ**こと —
    /// 停止処理の続き（`LastResultPath` の確定・終了確認の `Close()`）がその戻りを待っている。
    /// </summary>
    public event Action? RecordingMetadataRequested;

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
    /// 停止処理の完了後、録音データがあれば入力を促す（REQ-REC-13）。
    /// 既定値を入れてイベントを上げる。<c>MainWindow</c> が同期的にダイアログを出し、
    /// 閉じたら <see cref="CompleteRecordingMetadata"/> を呼ぶ。
    /// </summary>
    private void RequestRecordingMetadata(RecordingSession session)
    {
        MetadataMeetingName = "";
        MetadataHeldAt = RecordingMetadataFile.HeldAtText(session.StartedAt, session.StoppedAt ?? DateTime.Now);
        MetadataParticipantsText = "";
        MetadataTargetName = System.IO.Path.GetFileName(session.FilePath);
        RecordingMetadataRequested?.Invoke();
    }

    /// <summary>
    /// ダイアログが閉じた（REQ-REC-13）。<paramref name="accepted"/> が「OK」なら JSON を書き、
    /// 会議名があれば `.mp3` / `.txt` を改名して <see cref="LastResultPath"/> を追従させる（REQ-META-02）。
    /// 「キャンセル」なら何も残さない。
    /// </summary>
    public void CompleteRecordingMetadata(bool accepted)
    {
        var session = _audioCaptureService.CurrentSession;
        if (!accepted || session == null)
        {
            return;
        }

        // 改名先が既にある・失敗 → 元の名前のまま JSON だけ書く（REQ-META-02）
        var renameError = _audioCaptureService.RenameSessionFiles(MetadataMeetingName);
        var mp3Path = session.FilePath;
        var txtPath = System.IO.Path.ChangeExtension(mp3Path, ".txt");
        var jsonPath = RecordingMetadataFile.BuildMetadataPath(mp3Path);

        try
        {
            RecordingMetadataFile.Write(
                jsonPath,
                BuildMetadata(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText,
                    _settings.ParticipantDomainSortedLast));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"メタデータの保存に失敗しました: {ex.Message}";
            return;
        }

        // REQ-OPEN-01: 成果物のパスを改名後へ追従させる
        var hasTxt = System.IO.File.Exists(txtPath);
        LastResultPath = hasTxt ? txtPath : mp3Path;
        StatusMessage = renameError == null
            ? $"保存完了: {mp3Path} (メタデータ: {System.IO.Path.GetFileName(jsonPath)})"
            : $"{renameError} メタデータは {System.IO.Path.GetFileName(jsonPath)} に保存しました";
    }
}