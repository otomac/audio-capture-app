# AudioCaptureApp

Windows向け音声キャプチャアプリ。WASAPI Shared Mode で入力デバイスの音声を MP3 ファイルに録音します。

## 機能

- Windows の音声入力デバイスを一覧から選択
- WASAPI Shared Mode で録音（デバイスを占有しない、Teams/Zoom と同時使用可能）
- MP3 形式（LAME エンコード）でリアルタイム保存
- 保存先フォルダの指定・永続化
- 録音経過時間のリアルタイム表示

## 動作要件

- Windows 10 以降 (x64)
- .NET 8.0 Runtime

## ビルド

```bash
dotnet build AudioCaptureApp.sln
```

## 実行

```bash
dotnet run --project AudioCaptureApp
```

## 発行（スタンドアローン .exe）

```bash
dotnet publish AudioCaptureApp/AudioCaptureApp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## 設定ファイル

- 保存場所: `%APPDATA%\AudioCaptureApp\settings.json`
- デフォルト保存先: `%USERPROFILE%\Documents\AudioCapture`

## 使用ライブラリ

- [NAudio](https://github.com/naudio/NAudio) - 音声録音 (WASAPI)
- [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame) - MP3 エンコード
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM フレームワーク

## ライセンス

NAudio.Lame は LGPL ライセンスの LAME を使用しています。商用配布時は要確認。
