# AudioCaptureApp

Windows向け音声キャプチャアプリ。WASAPI Shared Mode で入力デバイスの音声を MP3 ファイルに録音します。

また、Whisper を利用してリアルタイムに簡易的な文字起こしができます。

## 機能

- Windows の音声入力デバイス（マイク ＆ スピーカー）を一覧から選択
- WASAPI Shared Mode で録音（デバイスを占有しない、Teams/Zoom と同時使用可能）
- MP3 形式（LAME エンコード）でリアルタイム保存
- 保存先フォルダの指定・永続化
- 録音経過時間のリアルタイム表示
- Whisper を用いた簡易リアルタイム文字起こし（マイクとスピーカーの識別あり、同一入力音声内の話者識別は無し）
- Whisper を用いた音声ファイルからの簡易文字起こし（音声チャネルは１つ）
- 音声ファイルからの文字起こしに限り、sherpa-onnx による話者ダイアライゼーション（同一音声内で `話者1` / `話者2` … を区別）を任意で有効にできる（既定は無効）

## 画面キャプチャ

![Main Screen](manual/image/main-screen.png)

## 動作要件

- Windows 10 以降 (x64)
- .NET 10.0 Runtime（Release 版の zip は self-contained のため、別途インストールは不要）

## インストール

1. [Release](https://github.com/otomac/audio-capture-app/releases) ページから最新版 zip ファイルをダウンロードする
2. 任意のフォルダに zip ファイルを展開する
3. `AudioCaptureApp.exe` を起動する

## Whisper モデルファイルの取得

- 本ツールのリリース媒体には、Whisper のモデルファイルは含まれていません
- 以下を参考に、別途モデルファイルを取得してください
  1. https://github.com/ggml-org/whisper.cpp からリポジトリを clone または Zip ダウンロードする
  2. コマンドラインから、 `models/download-ggml-model.cmd <size>` を実行し、ファイルを取得する
- `<size>` は `small` を推奨しますが、マシンスペックによって適切なモデルを選択してください

## 話者ダイアライゼーション（任意・既定は無効）

音声ファイルからの文字起こしに限り、**同一音声内で話者を区別**して出力できます。

```text
[00:00:01 - 00:00:04] [ファイル] [話者1] 本日の会議を始めます。
[00:00:05 - 00:00:08] [ファイル] [話者2] 最初に一点確認があります。
[00:00:08 - 00:00:12] [ファイル] [話者1] はい、お願いします。
```

### 前提と制約

- **完全にローカルで処理します。** クラウド API は使わず、音声や文字起こし内容を外部へ送信しません。
  ネットワーク接続が無い環境でも動作します。
- **モデルファイルを実行時にダウンロードしません。** 事前にローカルへ配置しておく必要があります。
- **話者 ID はその音声ファイルの中でのみ有効です。** 別のファイルの `話者1` が
  同一人物であることを意味しません。
- **話者の実名は推定しません**（Speaker Identification は行っていません）。声紋の登録も個人認証も行いません。
- **録音中のリアルタイム文字起こしには話者は付きません。** 使用している Diarization API が
  音声全体を必要とするためです。録音した MP3 を後からファイル文字起こしに掛けてください。
- 有効時は音声全体をメモリに載せるため、**約 230MB/時間** を消費します。

### モデルファイルの取得

本ツールのリリース媒体にモデルファイルは含まれていません。以下から 2 種類を取得してください。

1. **話者区間検出（segmentation）モデル** — pyannote 系
   <https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-segmentation-models>
2. **話者埋め込み（speaker embedding）モデル** — 3D-Speaker 系など
   <https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models>

ダウンロードした `.onnx` を任意のフォルダへ置きます（既定の想定は
`%APPDATA%\AudioCaptureApp\models\diarization\`）。

### 設定

`%APPDATA%\AudioCaptureApp\settings.json` を直接編集します（UI からは切り替えできません）。

```json
{
  "SpeakerDiarizationEnabled": true,
  "SpeakerSegmentationModelPath": "C:\\Users\\<user>\\AppData\\Roaming\\AudioCaptureApp\\models\\diarization\\segmentation.onnx",
  "SpeakerEmbeddingModelPath": "C:\\Users\\<user>\\AppData\\Roaming\\AudioCaptureApp\\models\\diarization\\embedding.onnx",
  "SpeakerClusteringThreshold": 0.5,
  "KnownSpeakerCount": null,
  "SpeakerDiarizationThreads": 1
}
```

| キー | 意味 |
|---|---|
| `SpeakerDiarizationEnabled` | 話者ダイアライゼーションを行うか。`false`（既定）なら従来どおりの出力になる |
| `SpeakerSegmentationModelPath` | 話者区間検出モデルの絶対パス |
| `SpeakerEmbeddingModelPath` | 話者埋め込みモデルの絶対パス |
| `SpeakerClusteringThreshold` | 話者数が未知のときの分割のしやすさ（既定 0.5）。**小さくすると話者を細かく分け、大きくするとまとめます。**話者が分かれすぎるなら上げ、別人が混ざるなら下げてください |
| `KnownSpeakerCount` | 参加者数が分かっている場合に指定する。`null`（既定）なら閾値による自動判定 |
| `SpeakerDiarizationThreads` | 推論スレッド数（既定 1）。長い音声で遅い場合に増やしてください |

モデルが見つからない場合や読み込めない場合は、どのモデルで失敗したかを示すエラーが出て
文字起こし自体が中止されます（話者欄が欠けた成果物を作らないため）。

### ライセンスの確認

**sherpa-onnx 本体のライセンスと、モデルのライセンスは別物です。混同しないでください。**

- sherpa-onnx（Apache-2.0）: <https://github.com/k2-fsa/sherpa-onnx/blob/master/LICENSE>
- 各モデル: 配布元のリリースページおよび元モデル（pyannote / 3D-Speaker 等）の
  ライセンス表記を**個別に確認してください**。モデルを再配布する場合は、必要な LICENSE / NOTICE を
  同梱する必要があります。本リポジトリはモデルを同梱していないため、その責任は配布者にあります。

## 開発の手引き

以降のビルド手順は、ローカルPC上に .NET SDK がインストールされていることが前提です。

### ビルド

```bash
dotnet build AudioCaptureApp.slnx
```

### 実行（動作確認）

```bash
dotnet run --project AudioCaptureApp
```

### 発行（スタンドアローン .exe）

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
- [Whisper.cpp](https://github.com/ggml-org/whisper.cpp) - OpenAI Whisper（文字起こしライブラリ）の C/C++実装
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) - 話者ダイアライゼーション（Apache-2.0）。ネイティブランタイムは win-x64 のみ同梱

## ライセンス

MIT License
Copyright (c) 2026 otomac

NAudio.Lame は LGPL ライセンスの LAME を使用しています。商用配布時は要確認。

話者ダイアライゼーション用のモデルファイルは本リポジトリに含まれていません。利用・再配布の条件は各モデルの配布元で確認してください（sherpa-onnx 本体のライセンスとは別です）。
