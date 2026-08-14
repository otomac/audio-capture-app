# AudioCaptureApp

## プロジェクト概要
Windows 向け音声キャプチャアプリ。マイク入力とスピーカー出力（ループバック）を WASAPI 経由で
キャプチャし、1 つの MP3 にミキシングして保存する。Whisper.net によるローカル日本語文字起こしを併せ持つ。
C# / WPF / NAudio で実装する。

---

## 進め方（最優先）

**すべての作業は `docs/harness/` の 4 つの法に従う。** 本文は
[docs/harness/00-ways-of-working.md](docs/harness/00-ways-of-working.md)。個別の指示より優先度が高い。

1. **タスク先行** — タスクを起票していない作業はしない。進捗は
   [docs/tasks/backlog.md](docs/tasks/backlog.md) でのみ管理する（会話中の「終わった」は進捗ではない）。
2. **仕様書先行** — 仕様が変わるなら、ソースより先に [docs/spec/](docs/spec/) を直す。
   `docs/spec/` が**唯一の正**。ただし仕様書とコードが食い違ったら**コードが事実**であり、
   「仕様書が古い」バグとして起票する。
3. **アーキテクチャ優先** — 3 層構成と依存方向を実装の都合で曲げない。変えるなら実装前に
   [docs/adr/](docs/adr/) へ ADR を書いて承認を得る。
4. **品質ゲート** — 下の 3 つを全部通すまで「完了」と言わない。**無効化して通すのは禁止**。

作業手順の全体は [docs/harness/10-workflow.md](docs/harness/10-workflow.md)。

### 品質ゲート（3 つとも必須）

```powershell
dotnet build  AudioCaptureApp.slnx -c Debug              # 警告 0 件（警告はエラー扱い）
dotnet format AudioCaptureApp.slnx --verify-no-changes   # 差分なし
dotnet test   AudioCaptureApp.slnx -c Debug              # 全件成功
```

- 定義と既知の穴: [docs/harness/40-quality-gates.md](docs/harness/40-quality-gates.md)
- **実行結果を示さずに「通りました」と言わない。** 件数を書く（「42 件成功 / 0 件失敗」）。

### 不可逆な操作

`git commit` / `push` / `add` / ブランチ切替 / PR 作成は、**その都度**明示的に依頼されたときだけ行う。
1 つの承認が別の操作の承認になることはない。

### ライブラリ追加は個別承認制

新しい NuGet パッケージを承認なしに導入・前提・既定にしない。BCL か最小限の自前実装を優先する。

---

## 技術スタック
- 言語: C# 12 / .NET 8（`net8.0-windows`）
- UI: WPF + CommunityToolkit.Mvvm（MVVM パターン）
- 録音: NAudio + NAudio.Wasapi（WASAPI Shared Mode）
- MP3: NAudio.Lame（`LameMP3FileWriter`）
- 文字起こし: Whisper.net + Runtime / Runtime.Cuda / Runtime.Vulkan
- 設定: System.Text.Json（`settings.json`）
- テスト: xUnit（`AudioCaptureApp.Tests`）

バージョンは [Directory.Packages.props](Directory.Packages.props) に一元管理（CPM）。
`.csproj` に `Version` 属性を書かない。

## アーキテクチャ方針
規範は [docs/harness/20-architecture-standards.md](docs/harness/20-architecture-standards.md)、
現状の記述は [docs/spec/02_architecture.md](docs/spec/02_architecture.md)、
背景は [ADR-0001](docs/adr/0001-baseline-architecture.md)。

- Models / ViewModels / Services の 3 層構成
- ViewModel は `MainViewModel.cs` 1 ファイルに集約（シンプル優先）
- **DI コンテナ・Service のインターフェース抽象は意図的に不使用**（ADR-0001）。導入したくなったら ADR を書く
- NAudio を直接使用する（独自抽象化レイヤーを作らない）
- **UI スレッド以外からバインドプロパティを更新しない。** 必ず
  `Application.Current.Dispatcher.BeginInvoke` を経由する
- オーディオコールバック内で重い処理・ブロッキングをしない

## コーディング標準
[docs/harness/30-coding-standards.md](docs/harness/30-coding-standards.md)。
機械強制は [.editorconfig](.editorconfig) と [Directory.Build.props](Directory.Build.props)。

## ビルド・実行

```powershell
dotnet build AudioCaptureApp.slnx
dotnet run --project AudioCaptureApp
```

ソリューションは **`.slnx`**（XML 形式）。`.sln` は存在しないので参照しないこと。

## ドキュメントの置き場

| 置き場 | 内容 |
|---|---|
| [docs/harness/](docs/harness/) | **開発ハーネス（規範）**。まずここを読む |
| [docs/spec/](docs/spec/) | 現行仕様書（唯一の正） |
| [docs/tasks/](docs/tasks/) | タスク台帳と実装計画 |
| [docs/adr/](docs/adr/) | アーキテクチャ決定記録 |
| [docs/archive/](docs/archive/) | 過去の成果物。参照専用、更新しない |
