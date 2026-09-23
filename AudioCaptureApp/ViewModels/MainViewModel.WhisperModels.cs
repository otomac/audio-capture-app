using System.Collections.ObjectModel;
using System.Globalization;
using AudioCaptureApp.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、Whisper モデルの登録一覧（エイリアス）、ライブ用の選択、モデル管理ダイアログの編集状態を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D / ADR-0006）。
public partial class MainViewModel
{
    // --- 登録一覧とライブ用の選択 (T162 / REQ-CFG-08 / REQ-MODELWIN-07) ---

    /// <summary>登録済み Whisper モデル（REQ-CFG-08）。設定ウィンドウのドロップダウンとモデル管理ダイアログが見る。</summary>
    public ObservableCollection<WhisperModelEntry> WhisperModels { get; } = new();

    /// <summary>
    /// ライブ文字起こしに使うモデル（REQ-MODELWIN-07）。変えると <see cref="WhisperModelPath"/> を更新して
    /// 保存し、読み込み直す。<c>null</c> は「モデル未設定」。
    /// </summary>
    [ObservableProperty]
    private WhisperModelEntry? _selectedWhisperModel;

    partial void OnSelectedWhisperModelChanged(WhisperModelEntry? value)
    {
        if (_initializing || _suppressWhisperModelSelectionWriteBack)
        {
            return;
        }

        // D1: 選択は実パスで表す。旧バージョンの WhisperModelPath と同じキーに書き続ける。
        WhisperModelPath = value?.ModelPath ?? string.Empty;
        SaveSettings();
        if (value == null)
        {
            // REQ-MODELWIN-04: 別のモデルへ勝手に乗り換えず「モデル未設定」へ倒す
            _transcriptionService.UnloadModel();
            _audioCaptureService.SetTranscriptionService(null);
            TranscriptionStatus = "モデル未設定";
            TranscribeFromFileCommand.NotifyCanExecuteChanged();
        }
        else
        {
            TryLoadWhisperModel();
        }
    }

    /// <summary>一覧の差し替えで選択を動かすとき、書き戻し（保存・再読み込み）を抑止する。</summary>
    private bool _suppressWhisperModelSelectionWriteBack;

    /// <summary>
    /// 旧バージョンの設定（一覧が無く <c>WhisperModelPath</c> だけ）を一覧へ移行する（REQ-CFG-08）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>一覧が無く（<c>null</c> または空）パスがある → ファイル名（拡張子なし）を名前にして 1 件</item>
    /// <item>一覧はあるがパスがどの要素とも一致しない（手編集）→ 同じく 1 件を足す</item>
    /// <item>パスが空 → 何も足さない</item>
    /// </list>
    /// 足す名前が既存と重複したら <c>名前 (2)</c> のように連番を付ける（起動時に検証エラーで止まれないため。D5）。
    /// 入力の要素はそのまま返す（コピーしない）。
    /// </remarks>
    internal static List<WhisperModelEntry> MigrateWhisperModelList(
        IEnumerable<WhisperModelEntry>? list, string? selectedPath)
    {
        var result = new List<WhisperModelEntry>();
        if (list != null)
        {
            foreach (var entry in list)
            {
                if (entry != null && !string.IsNullOrWhiteSpace(entry.ModelPath))
                {
                    result.Add(entry);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(selectedPath) || result.Any(e => SamePath(e.ModelPath, selectedPath)))
        {
            return result;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(selectedPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = selectedPath;
        }

        var name = baseName;
        for (int n = 2; result.Any(e => string.Equals(e.ModelName, name, StringComparison.Ordinal)); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({n})");
        }

        result.Add(new WhisperModelEntry { ModelName = name, ModelPath = selectedPath });
        return result;
    }

    /// <summary>
    /// 追加・名前変更の検証（REQ-CFG-08 / REQ-MODELWIN-02 / 03）。通れば <c>null</c>、通らなければ理由。
    /// </summary>
    /// <param name="existing">現在の一覧。</param>
    /// <param name="name">登録しようとする名前。</param>
    /// <param name="path">登録しようとするパス。</param>
    /// <param name="self">名前変更のときの対象要素（自分自身を重複と数えないため）。追加なら <c>null</c>。</param>
    /// <param name="fileExists">パスの存在検査。省略時は <see cref="System.IO.File.Exists(string)"/>。</param>
    internal static string? ValidateWhisperModelEntry(
        IEnumerable<WhisperModelEntry> existing,
        string name,
        string path,
        WhisperModelEntry? self,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "名前を入力してください。";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "モデルファイルを選択してください。";
        }

        if (!(fileExists ?? System.IO.File.Exists)(path))
        {
            return "モデルファイルが見つかりません。";
        }

        foreach (var entry in existing)
        {
            if (ReferenceEquals(entry, self))
            {
                continue;
            }

            if (string.Equals(entry.ModelName, name.Trim(), StringComparison.Ordinal))
            {
                return "同じ名前が既に登録されています。";
            }

            if (SamePath(entry.ModelPath, path))
            {
                return $"同じファイルが別の名前で登録されています: {entry.ModelName}";
            }
        }

        return null;
    }

    /// <summary>パスの同一性。同じファイルを別の表記で 2 度登録させない（REQ-CFG-08）。</summary>
    private static bool SamePath(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>一覧から実パスに一致する要素を引く。無ければ <c>null</c>。</summary>
    private static WhisperModelEntry? FindWhisperModel(IEnumerable<WhisperModelEntry> list, string path)
        => string.IsNullOrWhiteSpace(path) ? null : list.FirstOrDefault(e => SamePath(e.ModelPath, path));

    // --- モデル管理ダイアログ (T162 / REQ-MODELWIN-01〜06) ---

    /// <summary>
    /// モデル管理ダイアログを開いてほしい、という要求（REQ-MODELWIN-01）。
    /// 購読するのは <c>SettingsWindow</c> のコードビハインド（モーダルの上に出すため。ADR-0006 規則 4）。
    /// </summary>
    public event Action? WhisperModelsRequested;

    [RelayCommand]
    private void ShowWhisperModels()
    {
        ManagedWhisperModel = null;
        EditingModelName = "";
        EditingModelPath = "";
        WhisperModelError = "";
        WhisperModelsRequested?.Invoke();
    }

    /// <summary>
    /// ダイアログの一覧で選んでいる要素（名前変更・削除・並び替えの対象）。
    /// 非 null ならフォームは「名前の変更」モード、null なら「追加」モード（REQ-MODELWIN-02）。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseWhisperModelFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveWhisperModelUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveWhisperModelDownCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditingWhisperModel))]
    private WhisperModelEntry? _managedWhisperModel;

    /// <summary>フォームが「名前の変更」モードか（REQ-MODELWIN-02）。選択中に「追加」を押せる状態を作らない。</summary>
    public bool IsEditingWhisperModel => ManagedWhisperModel != null;

    partial void OnManagedWhisperModelChanged(WhisperModelEntry? value)
    {
        // 選んだ要素の名前とパスを欄へ写す（名前変更の起点）。選択を外したら追加モードに戻し、欄を空にする。
        if (value != null)
        {
            EditingModelName = value.ModelName;
            EditingModelPath = value.ModelPath;
        }
        else
        {
            EditingModelName = "";
            EditingModelPath = "";
        }
        WhisperModelError = "";
    }

    /// <summary>「新しく追加」（REQ-MODELWIN-02）。選択を外して追加モードへ戻す。</summary>
    [RelayCommand]
    private void NewWhisperModel()
    {
        ManagedWhisperModel = null;
    }

    private bool CanMoveWhisperModelUp => ManagedWhisperModel != null && WhisperModels.IndexOf(ManagedWhisperModel) > 0;

    private bool CanMoveWhisperModelDown =>
        ManagedWhisperModel != null && WhisperModels.IndexOf(ManagedWhisperModel) < WhisperModels.Count - 1;

    /// <summary>「上へ」（REQ-MODELWIN-08）。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveWhisperModelUp))]
    private void MoveWhisperModelUp() => MoveManagedWhisperModel(-1);

    /// <summary>「下へ」（REQ-MODELWIN-08）。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveWhisperModelDown))]
    private void MoveWhisperModelDown() => MoveManagedWhisperModel(+1);

    /// <summary>
    /// 選択中の要素を 1 つ動かしてその場で保存する。ライブ用の選択（`SelectedWhisperModel`）は
    /// 同じインスタンスのままなので変わらないが、`Move` の通知で ComboBox が選択を触ることがあるため書き戻しを抑止する。
    /// </summary>
    private void MoveManagedWhisperModel(int delta)
    {
        var target = ManagedWhisperModel;
        if (target == null)
        {
            return;
        }

        var index = WhisperModels.IndexOf(target);
        var newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= WhisperModels.Count)
        {
            return;
        }

        var live = SelectedWhisperModel;
        _suppressWhisperModelSelectionWriteBack = true;
        try
        {
            WhisperModels.Move(index, newIndex);
            SelectedWhisperModel = live;
            ManagedWhisperModel = target;
        }
        finally
        {
            _suppressWhisperModelSelectionWriteBack = false;
        }
        MoveWhisperModelUpCommand.NotifyCanExecuteChanged();
        MoveWhisperModelDownCommand.NotifyCanExecuteChanged();
        SaveSettings();
    }

    /// <summary>名前欄。追加ではエイリアス、名前変更では新しい名前。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameWhisperModelCommand))]
    private string _editingModelName = "";

    /// <summary>パス欄（読み取り専用）。「参照…」で入る。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWhisperModelCommand))]
    private string _editingModelPath = "";

    /// <summary>検証に通らなかった理由（REQ-MODELWIN-02）。空なら表示しない。</summary>
    [ObservableProperty]
    private string _whisperModelError = "";

    // 「名前の変更」モードではパスを変えられない（REQ-MODELWIN-03）
    private bool CanBrowseWhisperModelFile => ManagedWhisperModel == null;

    /// <summary>「参照…」（REQ-MODELWIN-02）。名前欄が空ならファイル名を既定の名前として入れる（D8）。</summary>
    [RelayCommand(CanExecute = nameof(CanBrowseWhisperModelFile))]
    private void BrowseWhisperModelFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Whisperモデルファイルを選択",
            Filter = "GGMLモデル (*.bin)|*.bin|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        EditingModelPath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(EditingModelName))
        {
            EditingModelName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        }
        WhisperModelError = "";
    }

    private bool CanAddWhisperModel =>
        ManagedWhisperModel == null
        && !string.IsNullOrWhiteSpace(EditingModelName) && !string.IsNullOrWhiteSpace(EditingModelPath);

    /// <summary>「追加」（REQ-MODELWIN-02）。検証に通ったものだけ足し、その場で保存する（REQ-MODELWIN-06）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddWhisperModel))]
    private void AddWhisperModel()
    {
        var error = ValidateWhisperModelEntry(WhisperModels, EditingModelName, EditingModelPath, self: null);
        if (error != null)
        {
            WhisperModelError = error;
            return;
        }

        var entry = new WhisperModelEntry { ModelName = EditingModelName.Trim(), ModelPath = EditingModelPath.Trim() };
        WhisperModels.Add(entry);
        SaveSettings();
        // REQ-TRX-FILE-01: 可否は「登録済みモデルが 1 つ以上」で決まる
        TranscribeFromFileCommand.NotifyCanExecuteChanged();

        EditingModelName = "";
        EditingModelPath = "";
        WhisperModelError = "";

        // 未設定だったなら、初めて登録したモデルをそのまま使う（選ぶ手間を 1 つ減らす）
        if (SelectedWhisperModel == null && WhisperModels.Count == 1)
        {
            SelectedWhisperModel = entry;
        }
    }

    private bool CanRenameWhisperModel =>
        ManagedWhisperModel != null && !string.IsNullOrWhiteSpace(EditingModelName);

    /// <summary>「名前を変更」（REQ-MODELWIN-03）。パスは変えない。</summary>
    [RelayCommand(CanExecute = nameof(CanRenameWhisperModel))]
    private void RenameWhisperModel()
    {
        var target = ManagedWhisperModel;
        if (target == null)
        {
            return;
        }

        var error = ValidateWhisperModelEntry(WhisperModels, EditingModelName, target.ModelPath, self: target);
        if (error != null)
        {
            WhisperModelError = error;
            return;
        }

        // D7: POCO なので同じ位置へ新しいインスタンスを差し替えて通知させる。
        var renamed = new WhisperModelEntry { ModelName = EditingModelName.Trim(), ModelPath = target.ModelPath };
        var index = WhisperModels.IndexOf(target);
        var wasSelected = ReferenceEquals(SelectedWhisperModel, target);

        // 名前だけの変更でパスは同じなので、保存・再読み込み・破棄はしない。
        // 差し替え（Replace）の最中に ComboBox が選択をいったん null にしうるため、抑止は差し替えの前から掛ける。
        _suppressWhisperModelSelectionWriteBack = true;
        try
        {
            WhisperModels[index] = renamed;
            if (wasSelected)
            {
                SelectedWhisperModel = renamed;
            }
        }
        finally
        {
            _suppressWhisperModelSelectionWriteBack = false;
        }
        ManagedWhisperModel = renamed;
        // REQ-CFG-09: ファイル用の保存名が旧名なら追随する
        if (string.Equals(_settings.FileWhisperModelName, target.ModelName, StringComparison.Ordinal))
        {
            _settings.FileWhisperModelName = renamed.ModelName;
        }
        SaveSettings();
        WhisperModelError = "";
    }

    private bool CanRemoveWhisperModel => ManagedWhisperModel != null;

    /// <summary>「削除」（REQ-MODELWIN-04）。選択中のモデルなら選択を解除し「モデル未設定」へ倒す。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveWhisperModel))]
    private void RemoveWhisperModel()
    {
        var target = ManagedWhisperModel;
        if (target == null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedWhisperModel, target);
        WhisperModels.Remove(target);
        ManagedWhisperModel = null;
        EditingModelName = "";
        EditingModelPath = "";
        WhisperModelError = "";
        // REQ-CFG-09: ファイル用の保存名が消えた要素なら、ライブ用へ倒す（null）
        if (string.Equals(_settings.FileWhisperModelName, target.ModelName, StringComparison.Ordinal))
        {
            _settings.FileWhisperModelName = null;
        }
        TranscribeFromFileCommand.NotifyCanExecuteChanged();

        if (wasSelected)
        {
            // OnSelectedWhisperModelChanged(null) が WhisperModelPath を空にして保存し、モデルを破棄する
            SelectedWhisperModel = null;
        }
        else
        {
            SaveSettings();
        }
    }
}