# AudioCaptureApp

## プロジェクト概要
Windows 向け音声キャプチャアプリ。C# / WPF / NAudio で実装する。

## 技術スタック
- 言語: C# 12 / .NET 8
- UI: WPF + CommunityToolkit.Mvvm (MVVM パターン)
- 録音: NAudio 2.2.1 + NAudio.Wasapi 2.2.1 (WASAPI Shared Mode)
- MP3: NAudio.Lame 2.1.0 (LameMP3FileWriter)
- 設定: System.Text.Json (settings.json)

## アーキテクチャ方針
- Models / ViewModels / Services の 3 層構成
- ViewModel は MainViewModel.cs 1 ファイルに集約（シンプル優先）
- NAudio を直接使用する（独自抽象化レイヤーを作らない）
- UI スレッドへのアクセスは必ず Dispatcher 経由で行う

## ビルド・実行
dotnet build
dotnet run

## 仕様書・タスク管理
- 仕様書: [Obsidian vault の spec.md へのパス or 相対パス]
- タスク: tasks.md （完了したものは [x] に更新すること）

## 開発ルール
- タスクは tasks.md の順番通りに 1 つずつ実装する
- 各タスク完了後に dotnet build で確認する
- エラーが出たら即座に修正してから次のタスクへ進む