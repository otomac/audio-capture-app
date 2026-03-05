# Tasks: 001-windows-audio-capture

Created: 2026-03-02
Status: Pending
Spec: [spec.md](./spec.md)
Plan: [plan.md](./plan.md)

凡例:
- `[P]` = 他タスクと並列実行可能
- `[US1]` = 対応する User Story
- `[ ]` = 未着手 / `[x]` = 完了

---

## Phase 1: プロジェクトセットアップ

- [x] T010 WPF プロジェクトを作成する (`dotnet new wpf -n AudioCaptureApp --framework net8.0-windows`)
- [x] T020 NuGet パッケージを追加する（NAudio / NAudio.Wasapi / NAudio.Lame / CommunityToolkit.Mvvm）
- [x] T030 フォルダ構造を作成する（Models / ViewModels / Services / assets）
- [x] T040 [P] アプリアイコン（.ico）を assets フォルダに配置する

---

## Phase 2: モデル・サービス層

- [x] T110 `AudioDevice.cs` モデルを実装する（DeviceId / FriendlyName / IsDefault）
- [x] T120 `AppSettings.cs` モデルを実装する（OutputFolder / LastSelectedDeviceId）
- [x] T130 `RecordingSession.cs` モデルを実装する（FilePath / StartedAt / StoppedAt / DeviceId）

- [x] T210 [US1] `AudioCaptureService` - `MMDeviceEnumerator` で入力デバイスを列挙する処理を実装する
- [x] T220 [US1] `AudioCaptureService` - `WasapiCapture` で WASAPI Shared Mode 録音を開始する処理を実装する
- [x] T230 [US2] `AudioCaptureService` - `LameMP3FileWriter` で MP3 エンコードしながらファイル書き込みする処理を実装する
- [x] T240 [US2] `AudioCaptureService` - ファイル名を `yyyyMMdd_HHmmss.mp3` で自動生成する処理を実装する
- [x] T250 [US2] `AudioCaptureService` - 録音停止・ファイルクローズ処理を実装する（IDisposable）
- [x] T260 `AudioCaptureService` - エラーハンドリングを実装する（デバイスなし / 排他モード / ディスク不足）

- [x] T310 [P] [US3] `SettingsService` - `%APPDATA%\AudioCaptureApp\settings.json` の読み書きを実装する
- [x] T320 [P] [US3] `SettingsService` - 設定ファイルが存在しない場合のデフォルト値（`%USERPROFILE%\Documents\AudioCapture`）を実装する

---

## Phase 3: ViewModel

- [x] T410 `MainViewModel` に `ObservableCollection<AudioDevice> Devices` プロパティを実装する
- [x] T420 `MainViewModel` に `SelectedDevice` / `IsRecording` / `OutputFolder` / `ElapsedTime` / `StatusMessage` プロパティを実装する
- [x] T430 [US1] `MainViewModel` に `StartRecordingCommand` を実装する（CanExecute: デバイス選択済み・録音中でない）
- [x] T440 [US2] `MainViewModel` に `StopRecordingCommand` を実装する（CanExecute: 録音中）
- [x] T450 [US3] `MainViewModel` に `SelectOutputFolderCommand` を実装する（フォルダダイアログ）
- [x] T460 [US1] `MainViewModel` に `RefreshDevicesCommand` を実装する
- [x] T470 [US2] `MainViewModel` に `DispatcherTimer` で録音経過時間（`ElapsedTime`）を 1 秒毎に更新する処理を実装する
- [x] T480 `MainViewModel` のコンストラクタでデバイス一覧を読み込む処理と設定を復元する処理を実装する

---

## Phase 4: UI（XAML）

- [x] T510 `MainWindow.xaml` に入力デバイス選択ドロップダウン（`ComboBox`）を実装する
- [x] T520 `MainWindow.xaml` に「更新」ボタンを実装する（`RefreshDevicesCommand` にバインド）
- [x] T530 `MainWindow.xaml` に保存先フォルダ表示テキストボックスと「選択」ボタンを実装する
- [x] T540 [US1] `MainWindow.xaml` に「録音開始」ボタンを実装する（録音中は無効化）
- [x] T550 [US2] `MainWindow.xaml` に「録音停止」ボタンを実装する（録音中のみ有効）
- [x] T560 [US2] `MainWindow.xaml` に録音経過時間ラベルを実装する（`ElapsedTime` バインド）
- [x] T570 `MainWindow.xaml` にステータスメッセージラベルを実装する（`StatusMessage` バインド）
- [x] T580 `MainWindow.xaml.cs` で `DataContext` に `MainViewModel` をセットする

---

## Phase 5: 動作確認・仕上げ

- [x] T610 [SC-001] アプリ起動 → デバイス一覧表示まで 3 秒以内であることを確認する
- [x] T620 [SC-002] 録音開始ボタン押下 → 1 秒以内に録音が始まることを確認する
- [x] T630 [SC-003] 録音停止後、指定フォルダに有効な MP3 ファイルが存在することを確認する
- [x] T640 [SC-004] Teams/Zoom 使用中に同デバイスで録音できることを確認する（WASAPI Shared Mode）
- [x] T650 [SC-005] アプリ再起動後に保存先フォルダ設定が保持されることを確認する
- [ ] T660 [SC-006] 長時間（30分以上）録音後に MP3 が正常再生できることを確認する ※手動テスト必要
- [x] T670 [P] README.md を作成する（セットアップ手順・使い方・ライセンス）
- [x] T680 [P] 発行設定（`dotnet publish` / Self-contained / Single file）を確認する
