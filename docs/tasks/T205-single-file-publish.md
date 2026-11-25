# T205 — リリース版を単一ファイルの exe で発行する

> **状態:** 完了 — 2026-11-25
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

今の CI は `PublishSingleFile` を付けずに発行しており、zip の直下に 281 件のファイル（`VERSION` / `README.md` を含む）が並ぶ。
単一ファイル（`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`）で発行し、展開したフォルダを見て何を起動すればよいかが分かる形にする。
ネイティブ DLL を自分で探す部品（Whisper.net・sherpa-onnx・NAudio.Lame）が、展開先が変わっても動くことを先に確かめる。

## 2. 調べたこと

検証は `C:\personal\dev\audio-capture-work\t205\`。

**そのまま単一ファイルにすると、Whisper のモデルを読み込めない。** ネイティブ DLL は exe から `%TEMP%\.net\AudioCaptureApp\<id>\` へ
展開されるが、Whisper.net はアプリのフォルダ（exe の隣）の `runtimes\` からランタイムを探すため、見つけられない。
画面下に「文字起こしエラー: Whisperモデル…」が出た。ほかに、使わない Linux 用の `.so`（`runtimes\cuda\linux-x64` / `runtimes\vulkan\linux-x64`、
123 MB）が exe の外に残った。これは今の zip にも入っている。

**Whisper のランタイムだけを exe の外に残す**と動いた（下の D1）。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | exe に入れないもの | **Whisper.net の `runtimes\`（win-x64 の cuda / vulkan / CPU）** — 上のとおり、exe に入れると読み込めない。**LAME の `libmp3lame.*.dll`** — LGPL のライブラリなので、今までどおり差し替えられる別ファイルのまま置く。どちらも `ResolvedFileToPublish` に `ExcludeFromSingleFile` を付ける（`AudioCaptureApp.csproj` の `KeepNativeRuntimesBesideSingleFile`） |
| D2 | 使わないランタイム | linux-x64・win-arm64・win-x86 の Whisper ランタイムは発行しない（今までは単一ファイルのときだけ win-arm64 / win-x86 を発行後に消していた。その `RemoveDir` は不要になったので消した） |
| D3 | sherpa-onnx・onnxruntime・WPF のネイティブ | exe に入れ、展開させる。通常の DLL の探し方で見つかるので動く（下の確認） |
| D4 | どこで切り替えるか | CI の発行コマンドに `/p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` を足す。ローカルの `dotnet build` / `dotnet run` は変わらない |

## 4. 仕様書への影響

- [x] `docs/spec/02_architecture.md` の「配布」の行（単一ファイル・exe に入れないもの・展開先）
- README の「発行」のコマンドと、exe の隣のファイルを動かさない注意

## 5. アーキテクチャへの影響

- ADR: 不要（発行の形だけ。コード・層・依存は変わらない。新しいパッケージは無い）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/AudioCaptureApp.csproj` | `KeepNativeRuntimesBesideSingleFile` を足し、`CleanPublishOutput` の `RemoveDir` を外す |
| `.github/workflows/build-desktop.yml` | 発行に `PublishSingleFile` / `IncludeNativeLibrariesForSelfExtract` |
| `docs/spec/02_architecture.md` / `README.md` | 上の 4 |

## 7. 確認

**発行物**（ローカルで CI と同じコマンド、zip も CI と同じ `Compress-Archive`）:

| | 今まで | 単一ファイル |
|---|---|---|
| ファイル数（発行物） | 505 件（直下 265 件） | **17 件**（exe・`libmp3lame` 2 件・`runtimes\` 14 件） |
| 発行物の合計 | 443 MB | 308 MB |
| zip | 237 MB・直下 281 件 | **154 MB・直下 6 件**（`AudioCaptureApp.exe`・`libmp3lame.32.dll`・`libmp3lame.64.dll`・`runtimes`・`VERSION`・`README.md`） |

**動くか**（`t205\singlefilecheck`。本体と同じ発行設定の検証プログラムを単一ファイルで発行し、中から本体の部品を動かす）:

| | GPU 指定 | CPU 指定 | GPU 用ランタイムを外した写し |
|---|---|---|---|
| MP3 を書く（NAudio.Lame） | OK | OK | OK |
| Whisper モデルの読み込み | OK（GPU (Vulkan)、exe の隣の `runtimes\vulkan`） | OK（CPU） | OK（CPU、`runtimes\win-x64`） |
| ライブ文字起こし（30 秒を実時間で流す） | 5 行 | 5 行 | 2 行（20 秒） |
| ファイル文字起こし・話者識別あり（90 秒） | 33 行・14 人 | 31 行・12 人 | 7 行・3 人（30 秒） |

- 単一ファイルにしない同じプログラムと、**ファイル文字起こしの出力がバイト単位で一致した**（GPU・CPU とも）。ライブの行数も同じ。
- sherpa-onnx と onnxruntime は展開先（`%TEMP%\.net\...`）から、`libmp3lame.64.dll` と Whisper のランタイムは exe の隣から読み込まれた。
- 本体の exe も起動し、画面下に「Whisperランタイム: GPU (Vulkan)」が出ることを見た（今までの形でもこの PC は Vulkan を選ぶ）。

**起動時間**（ウィンドウが出るまで。この PC）:

| | 今まで | 単一ファイル |
|---|---|---|
| 2 回目以降 | 1.8〜2.6 秒 | 3.0〜3.7 秒 |
| 新しい発行物の初回 | 29 秒 | 48〜152 秒（4 回） |

初回の遅さは展開ではなく、**新しい exe を初めて起動するときの検査**による（展開先だけを新しくすると 3.7 秒、同じ中身を別フォルダへ写した exe は 3.6 秒）。
リリースを入れ替えたあとの最初の 1 回だけ遅くなる。今までの形でも初回は 29 秒かかっていた。

> **残り:** CUDA のランタイムはこの PC では選ばれない（今までの形でも Vulkan になる）ので、CUDA で動くことは確かめていない。
> 読み込み先は Vulkan と同じ exe の隣の `runtimes\cuda\win-x64` で、置き方は今までと同じ。
> GPU の無い PC は、GPU 用ランタイムを外した写しで代えた。

---

## 実行結果 (2026-11-25)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 567 件成功 / 0 件失敗 / 0 件スキップ（追加なし。発行の設定だけの変更）
- 計画からの逸脱: なし
