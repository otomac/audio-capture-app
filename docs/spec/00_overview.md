# AudioCaptureApp 仕様書

## アプリケーション概要

Windows 向けのスタンドアロン音声キャプチャアプリケーション。
マイク入力とスピーカー出力（ループバック）を WASAPI 経由でキャプチャし、1 つの MP3 ファイルにミキシングして保存する。
録音と並行して、または録音済み／任意の音声ファイルに対して、Whisper（Whisper.net）によるローカル日本語文字起こしを行うことができる。

- 実行環境: Windows 10 以降 / .NET 8 (net8.0-windows) / WPF
- エントリーポイント: `AudioCaptureApp/App.xaml.cs` → `MainWindow`
- 構成: `Models` / `ViewModels` / `Services` の 3 層構成（[CLAUDE.md](../../CLAUDE.md) のアーキテクチャ方針に準拠）

## ドキュメント構成

| ファイル | 内容 |
|---|---|
| [01_requirements.md](./01_requirements.md) | 要件リスト（機能要件・非機能要件） |
| [02_architecture.md](./02_architecture.md) | ソフトウェアアーキテクチャ |
| [03_class_diagram.md](./03_class_diagram.md) | クラス図（Mermaid） |
| [04_sequence_diagram.md](./04_sequence_diagram.md) | シーケンス図（Mermaid） |

## 対象範囲外

- API 仕様（本アプリは外部公開 API を持たない単体デスクトップアプリのため対象外）
- データ仕様（詳細なファイルフォーマット定義は対象外）
- UI 仕様（画面レイアウト・デザインの詳細は対象外。操作フローは要件リスト／シーケンス図に含める）
