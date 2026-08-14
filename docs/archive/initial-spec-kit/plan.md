# Technical Plan: 001-windows-audio-capture

Created: 2026-03-02
Status: Draft
Spec: [spec.md](./spec.md)

---

## Summary

spec.md の要件をもとに、C# / WPF で Windows 専用の音声キャプチャアプリを実装する。
NAudio (WASAPI Shared Mode) で録音し、NAudio.Lame で MP3 エンコードする。
UIは MVVM パターンで実装し、デバイス選択・録音制御・設定永続化を担う。

---

## Technical Context

| 項目 | 内容 |
|------|------|
| 言語 | C# 12 / .NET 8 |
| UIフレームワーク | WPF (Windows Presentation Foundation) |
| 録音API | NAudio 2.2.1 + NAudio.Wasapi 2.2.1 (WASAPI Shared Mode) |
| MP3エンコード | NAudio.Lame 2.1.0 (LameMP3FileWriter) |
| アーキテクチャ | MVVM (CommunityToolkit.Mvvm) |
| 設定永続化 | System.Text.Json (JSON ファイル) |
| ターゲットOS | Windows 10 以降 (x64) |
| IDE | Visual Studio 2022 または JetBrains Rider |

### NuGet パッケージ

```xml
<PackageReference Include="NAudio" Version="2.2.1" />
<PackageReference Include="NAudio.Wasapi" Version="2.2.1" />
<PackageReference Include="NAudio.Lame" Version="2.1.0" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
```

---

## Project Structure

```
AudioCaptureApp/
├── AudioCaptureApp.sln
└── AudioCaptureApp/
    ├── AudioCaptureApp.csproj
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml / MainWindow.xaml.cs
    │
    ├── Models/
    │   ├── AudioDevice.cs          # デバイス情報モデル
    │   ├── RecordingSession.cs     # 録音セッション情報
    │   └── AppSettings.cs          # 設定データモデル
    │
    ├── ViewModels/
    │   └── MainViewModel.cs        # 唯一のViewModel（シンプル構成）
    │
    ├── Services/
    │   ├── AudioCaptureService.cs  # 録音制御（NAudio ラッパー）
    │   └── SettingsService.cs      # 設定の読み書き（JSON）
    │
    └── assets/
        └── app.ico
```

---

## Implementation Phases

### Phase 1: プロジェクトセットアップ

1. `dotnet new wpf -n AudioCaptureApp` でプロジェクト作成
2. NuGet パッケージの追加
3. フォルダ構造の作成（Models / ViewModels / Services）
4. `App.xaml` で CommunityToolkit.Mvvm の DI または手動インスタンス化を設定

---

### Phase 2: サービス層の実装

**AudioCaptureService** (FR-002, FR-003, FR-004, FR-005, FR-006)

```csharp
// 主要インターフェース
void RefreshDevices();                          // デバイス一覧の再スキャン
IReadOnlyList<AudioDevice> GetDevices();        // デバイス一覧取得
void StartRecording(AudioDevice device, string outputFolder);
void StopRecording();
bool IsRecording { get; }
```

- `MMDeviceEnumerator` で DataFlow.Capture デバイスを列挙
- `WasapiCapture(device)` でShared Mode録音（デフォルト = 非占有）
- `DataAvailable` イベントで `LameMP3FileWriter` にデータを書き込む
- ファイル名 = `DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp3"`

**SettingsService** (FR-007, FR-008, FR-009)

```csharp
AppSettings Load();
void Save(AppSettings settings);
```

- 設定ファイルパス: `%APPDATA%\AudioCaptureApp\settings.json`
- デフォルト OutputFolder: `%USERPROFILE%\Documents\AudioCapture`

---

### Phase 3: ViewModel の実装

**MainViewModel** (CommunityToolkit.Mvvm の `ObservableObject` を継承)

```csharp
// プロパティ
ObservableCollection<AudioDevice> Devices
AudioDevice? SelectedDevice
bool IsRecording
string OutputFolder
TimeSpan ElapsedTime     // 録音経過時間（DispatcherTimer で更新）
string StatusMessage

// コマンド
IRelayCommand StartRecordingCommand
IRelayCommand StopRecordingCommand
IRelayCommand SelectOutputFolderCommand
IRelayCommand RefreshDevicesCommand
```

---

### Phase 4: UI（XAML）の実装

**MainWindow.xaml のレイアウト**

```
┌─────────────────────────────────────┐
│  音声キャプチャ                        │
├─────────────────────────────────────┤
│ 入力デバイス: [ドロップダウン ▼] [更新] │
├─────────────────────────────────────┤
│ 保存先: C:\...\AudioCapture  [選択]  │
├─────────────────────────────────────┤
│      [● 録音開始] / [■ 停止]         │
│           00:00:00                   │
├─────────────────────────────────────┤
│ ステータス: 待機中                     │
└─────────────────────────────────────┘
```

- 録音中: 「録音開始」ボタンを無効化 / 「停止」ボタンを有効化
- 録音中: 経過時間を 1 秒ごとに更新
- デバイス未選択時: 「録音開始」ボタンを無効化

---

## Constitution Check

| 原則 | 適用 |
|------|------|
| Simplicity | 1プロジェクト構成。ViewModelは1ファイルに集約 |
| Anti-Abstraction | NAudio を直接使用。独自の録音フレームワークは作らない |
| Library-First | AudioCaptureService は独立したサービスクラスとして実装 |

---

## リスク・注意事項

| リスク | 対策 |
|--------|------|
| LAME の LGPL ライセンス | 個人利用・社内ツール用途のため問題なし。商用配布時は要確認 |
| WasapiCapture のスレッド | DataAvailable は別スレッドで発火。UI 更新は Dispatcher 経由で行う |
| 排他モードデバイス | `AudioClientShareMode.Shared` 指定で対応。それでも失敗した場合は例外キャッチして UI に通知 |
| 長時間録音 | LameMP3FileWriter はストリーミングで書き込むため、メモリ蓄積なし |
