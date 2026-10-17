using AudioCaptureApp.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

// SettingsViewModel のうち、速報（字幕帯）の有効化と速報用モデルの選択・読み込みを担当する部分（T186 / T187）。
// ViewModel はファイルを機能単位で partial に割ってよい（ADR-0008、1 ファイル 500 行が目安）。
public sealed partial class SettingsViewModel
{
    // --- 速報 (T186 / T187 / REQ-SETWIN-03 ⑦ / REQ-TRX-04 / REQ-CFG-12) ---

    /// <summary>「録音中に速報を出す」（REQ-SETWIN-03 ⑦）。変えた時点で保存し、速報用モデルを読み直す。次の録音から効く。</summary>
    [ObservableProperty]
    private bool _liveProvisionalEnabled;

    partial void OnLiveProvisionalEnabledChanged(bool value)
    {
        _main.TranscriptionService.ProvisionalEnabled = value;
        if (_initializing)
        {
            return;
        }

        _main.SaveSettings();
        ReloadProvisionalModel();
    }

    /// <summary>
    /// 速報用モデル（REQ-SETWIN-03 ⑦）。登録一覧の要素で、設定には名前で持つ（REQ-CFG-12）。<c>null</c> は未選択。
    /// </summary>
    [ObservableProperty]
    private WhisperModelEntry? _selectedProvisionalWhisperModel;

    partial void OnSelectedProvisionalWhisperModelChanged(WhisperModelEntry? value)
    {
        if (_initializing || _suppressWhisperModelSelectionWriteBack)
        {
            return;
        }

        _main.SaveSettings();
        ReloadProvisionalModel();
    }

    /// <summary>速報用モデルの状態（読み込み中・完了・失敗・未選択）。設定ウィンドウに出す。</summary>
    [ObservableProperty]
    private string _provisionalModelStatus = "";

    /// <summary>速報用モデルだけを読み直している間 <c>true</c>（<see cref="ReloadProvisionalModel"/>）。</summary>
    private bool _isLoadingProvisional;

    /// <summary>読み込み中に来た「もう 1 回読み直して」の印（T187 D7）。走っている読み込みが最後に拾う。</summary>
    private bool _provisionalReloadRequested;

    /// <summary>走っている速報用モデルだけの読み直し。ライブ用の読み込みはこれを待ってから始める（読み込みを重ねない）。</summary>
    private Task _provisionalLoad = Task.CompletedTask;

    /// <summary>
    /// 速報用モデルだけを読み直す（有効化・選択を変えたとき。T187 D7）。読み込み中なら印だけ付け、走っている読み込みに任せる。
    /// </summary>
    internal void ReloadProvisionalModel()
    {
        if (_isLoadingModel || _isLoadingProvisional)
        {
            _provisionalReloadRequested = true;
            return;
        }

        _provisionalLoad = RunProvisionalReloadAsync();
    }

    private async Task RunProvisionalReloadAsync()
    {
        _isLoadingProvisional = true;
        try
        {
            await TryLoadProvisionalModelAsync();
        }
        // CA1031: 呼び出し元は待たない（UI の変更ハンドラーから起動する）。Whisper のネイティブ読み込み境界の
        //         例外を漏らすと観測されないまま消えるので、画面の状態表示に変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            ProvisionalModelStatus = $"速報用モデル: {ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _isLoadingProvisional = false;
        }
    }

    /// <summary>
    /// 速報用モデルを読む。速報が無効・未選択なら破棄する（T186 D11）。読んでいる間に選択が変わったら、もう 1 度読む。
    /// 同じパスが読み込み済みならサービスが読み直さない（GPU の切り替えでは読み直さない。T186 D9）。
    /// </summary>
    /// <remarks>
    /// ライブ用モデルの読み込み（<see cref="TryLoadWhisperModel"/>）の成功の後と、<see cref="ReloadProvisionalModel"/> から呼ぶ。
    /// 読み込みを重ねないのは、ネイティブログの観測（GPU の判定。REQ-TRX-02）がプロセス全体で 1 つしか無いためである。
    /// </remarks>
    private async Task TryLoadProvisionalModelAsync()
    {
        var service = _main.TranscriptionService;
        do
        {
            _provisionalReloadRequested = false;
            var entry = LiveProvisionalEnabled ? SelectedProvisionalWhisperModel : null;
            if (entry == null)
            {
                service.UnloadProvisionalModel();
                ProvisionalModelStatus = LiveProvisionalEnabled ? "速報用モデル未選択（速報は動きません）" : "速報は無効";
                continue;
            }

            ProvisionalModelStatus = "速報用モデル読み込み中...";
            var path = entry.ModelPath;
            var error = await Task.Run(() => service.LoadProvisionalModel(path));
            ProvisionalModelStatus = error == null ? "速報用モデル読み込み完了（CPU）" : $"速報用モデル: {error}";
        }
        while (_provisionalReloadRequested);
    }

    /// <summary>一覧から名前に一致する要素を引く（REQ-CFG-08 の <c>Ordinal</c>）。無ければ <c>null</c>。</summary>
    internal static WhisperModelEntry? FindWhisperModelByName(IEnumerable<WhisperModelEntry> list, string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : list.FirstOrDefault(e => string.Equals(e.ModelName, name, StringComparison.Ordinal));
}