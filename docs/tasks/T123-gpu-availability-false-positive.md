# T123 — GPU が使えない環境でも「GPU 実行」と表示され、強制 OFF が発火しない

> **状態:** 完了 — 2026-08-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **発端:** [T119](./T119-gpu-toggle-ineffective.md) §8 で切り出した別件（当時は「疑い」）

## 1. 目的

`GpuAvailable` を `RuntimeOptions.LoadedLibrary` の種別だけで判定しているため、
**GPU 版ランタイム DLL が読み込めれば使える GPU が無くても「利用可能」と判定される。**
REQ-GPU-02（GPU 利用不可なら強制 OFF・UI 無効化）が発火せず、
実際は CPU で動いているのに `GPU (Vulkan)` と表示される。これを正す。

---

## 2. 調査結果

### 2.1 疑いは確定した（再現に成功）

Vulkan ローダーの ICD を無効化（`VK_DRIVER_FILES` / `VK_ICD_FILENAMES` を存在しない
ファイルに向ける）すると、**本機でも「Vulkan ランタイムは読めるが GPU デバイスが 0」**の
状態を作れた。その状態での実測:

```
LoadModel            = (success:True, gpuAvailable:True)   ← 誤り
RuntimeOptions.LoadedLibrary = Vulkan
RuntimeInfo 通知     = "GPU (Vulkan)"                       ← 誤り
文字起こし所要       = 7991 ms                              ← CPU 速度
```

GPU が有効なときは同条件で 2367 ms。**3.4 倍遅いのに「GPU 実行」と表示していた。**
`IsRuntimeSupported(Vulkan, "win", "x64")` はデバイスの有無を見ずに常に `True` を返し、
`ggml-vulkan-whisper.dll` 自体は GPU が無くても読み込めるため、こうなる。

### 2.2 判定に使える 2 つの信号（ネイティブログ）

`Whisper.net.Logger.LogProvider.AddLogger` で whisper.cpp / ggml のログを拾える。
3 パターンを実測して、**別々の 2 つの事実**が観測できることを確認した。

| ケース | `use gpu` | `backends` | `whisper_model_load: <X> total size` | 実測 |
|---|---|---|---|---|
| GPU あり / `UseGpu=true` | 1 | **2** | **Vulkan0** | 2367 ms |
| GPU 無し / `UseGpu=true` | 1 | **1** | **CPU** | 7991 ms |
| GPU あり / `UseGpu=false` | 0 | **2** | **CPU** | 8284 ms |

- **`backends = N`** … その環境で登録された**バックエンドの種類数**。
  GPU バックエンドが登録できたかを表し、`UseGpu` の指定に左右されない（3 行目参照）。
  → **`GpuAvailable`（この PC で GPU が使えるか）はこれで判定できる。**
- **`whisper_model_load: <X> total size`** … モデルの重みが**実際にどこに載ったか**。
  `Vulkan0` / `CPU` のように出る。
  → **実行先（ステータス表示）はこれで判定できる。**

つまり「GPU が使えるか」と「今 GPU で動いているか」は別々に取れる。
現在のコードは前者を DLL 種別で代用し、後者を `useGpu` 引数で代用していたため両方外れていた。

### 2.3 この手段の弱点

**whisper.cpp のログ文字列に依存する。** 公開 API ではないため、Whisper.net /
whisper.cpp の更新で書式が変わりうる。これを踏まえ、**解析できなかったときは
従来の判定へフォールバック**する（§4 D3）。パッケージは 1.9.0 に固定済み（§10）。

なお `Whisper.net` にはこれ以外の GPU 情報源が無い。
`WhisperFactory.GetRuntimeInfo()`（`whisper_print_system_info`）は CPU の命令セットしか
返さず、3 パターンすべてで同一文字列だった。

## 3. スコープ境界

**やること**

- `LoadModel` の読み込み中だけネイティブログを購読し、`backends` と重みの配置先を拾う
- `GpuAvailable` を `IsGpuLibrary(LoadedLibrary) && GPU バックエンドが登録された` に変更
- `RuntimeInfo` の実行先を **実際の重み配置**から決める（`useGpu` 引数からの推定をやめる）
- 解析できないときは従来判定へフォールバックする

**やらないこと（重要）**

- **ggml / Vulkan への直接 P/Invoke。** デバイス列挙を自前で行えば堅牢になるが、
  ネイティブ相互運用層の新設はアーキテクチャ変更であり ADR が必要 → 本タスクでは行わない
- **`MainViewModel` の強制 OFF ロジック。** `GpuAvailable` が正しくなれば既存コードが正しく動く
- **新規パッケージの追加。** `Whisper.net` 1.9.0 の公開 API だけで足りる
- **ログの外部出力・ログ基盤の導入。** 読み込み中の一時購読のみ

## 4. 決定事項

| # | 決定が必要だった点 | 結論 |
|---|---|---|
| D1 | GPU 利用可否の判定方法 | **`backends >= 2` かつ `IsGpuLibrary(LoadedLibrary)`。** 前者だけだと CPU ランタイムに BLAS バックエンドが載る環境で誤検知しうるため、両方を要求する |
| D2 | 実行先（表示）の判定方法 | **`whisper_model_load: <X> total size` の `<X>` が `CPU` 以外なら GPU 実行。** `useGpu` 引数は「要求」であって結果ではない |
| D3 | ログを解析できなかったときの挙動 | **従来の判定へフォールバック**（`GpuAvailable = IsGpuLibrary(loaded)`、実行先 = `useGpu && gpuAvailable`）。書式変更で機能が壊れるより、T119 時点の挙動へ退行するほうが安全 |
| D4 | 強制 OFF 後に GPU 判定を永続化するか | **しない。** 次回起動時は `UseGpu=false` で読み込むが `backends` は `UseGpu` に依らないため正しく再判定される（§2.2 の 3 行目）。ドライバ導入後などに自動で復帰できる |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - **REQ-TRX-02** 改訂 — GPU 利用可否の判定根拠を「読み込まれたランタイム種別」から
    「ランタイム種別 ＋ 実際に登録された GPU バックエンド」に変更
  - **REQ-GPU-02** 改訂 — 「GPU が利用不可と判明した場合」の判定方法を明記
  - **REQ-GPU-05** 改訂 — 通知する実行先を「実際に重みが載ったバックエンド」から決める旨を明記
- [x] `docs/spec/04_sequence_diagram.md` — §7 に「読み込み中だけネイティブログを購読」を追加
- [-] `docs/spec/02_architecture.md` — 影響なし（層構成・依存方向・スレッドモデル不変）
- [-] `docs/spec/03_class_diagram.md` — 影響なし（追加するのは `internal` の純粋関数のみ）

## 6. アーキテクチャへの影響

なし。`TranscriptionService` 内部で完結する。新しい層・依存方向・スレッドは増やさない
（ログコールバックは `FromPath` を呼んだスレッド上で同期的に発火する）。ADR: **不要**

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | ログ購読と 2 つの解析関数、`LoadModel` の判定変更 |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 解析関数のテスト（実測ログ文字列をそのまま使う） |
| `docs/spec/01_requirements.md` | §5 のとおり（**実装より先**） |
| `docs/spec/04_sequence_diagram.md` | §5 のとおり（**実装より先**） |

## 8. テスト一覧

解析を純粋関数に切り出し、**§2.2 で実測したログ行そのもの**を入力にする。

- **`ParseBackendCount_WhisperInitLine_ReturnsCount`** — `backends   = 2` から `2` を取れる
- **`ParseBackendCount_DeviceCountLine_ReturnsNull`** — `devices    = 5` を誤って拾わない
- **`ParseBackendCount_UnrelatedLine_ReturnsNull`** — 無関係な行では `null`
- **`ParseModelBackend_GpuPlacement_ReturnsBackendName`** — `Vulkan0 total size = ...` から `Vulkan0`
- **`ParseModelBackend_CpuPlacement_ReturnsCpu`** — `CPU total size = ...` から `CPU`
- **`ParseModelBackend_ModelSizeLine_ReturnsNull`** — `model size = ...` を誤って拾わない
- **`IsGpuInUse_*`** — `CPU` は false、`Vulkan0` / `CUDA0` は true、`null`（解析不能）は false

> **テストで守れない範囲:** 「GPU デバイスが 0 の環境で `GpuAvailable` が false になる」ことは
> Vulkan ICD を無効化した実環境でしか再現できない。§2.1 と「実行結果」の実測プローブで担保する。

## 9. 未解決の質問

なし（D1〜D4 で確定）。

## 10. 前提

- `Whisper.net` を **1.9.0 から上げない**。上げるときは §2.2 のログ書式を実測し直す
  （フォールバックがあるため壊れても T119 時点の挙動に退行するだけ）
- ログコールバックはプロセス全体で共有される静的イベントに載る。`LoadModel` の
  同時実行は `MainViewModel._isLoadingModel` が防いでいる前提
- 検証環境: Windows 11 / x64 / Vulkan 利用可・CUDA 利用不可

---

## 実行結果 (2026-08-18)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes` : 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **75** 件成功 / **0** 件失敗 / **0** 件スキップ

> 計測は `develop` 由来の `fix-gpu-toggle-and-model-load` ブランチ上で、T119 / T122 / T123 を
> まとめた最終状態に対して行った（3 件を 1 コミットにしたため中間状態は存在しない）。

- 計画からの逸脱: **なし**

### 修正後の実測プローブ（3 環境）

実アプリの `TranscriptionService.LoadModel` / `TranscribeFileAsync` をそのまま呼んだ結果。
「GPU 無し」は Vulkan ローダーの ICD を無効化して作った（§2.1）。

| 環境 / 設定 | `GpuAvailable` | `RuntimeInfo` | 所要 | 修正前 |
|---|---|---|---|---|
| GPU あり / GPU ON | `True` | `GPU (Vulkan)` | 2322 ms | 同じ（正しかった） |
| GPU あり / GPU OFF | `True` | `CPU` | 7892 ms | 同じ（T119 で修正済み） |
| **GPU 無し / GPU ON** | **`False`** | **`CPU`** | 7939 ms | **`True` / `GPU (Vulkan)`（誤り）** |

- 3 行目が本タスクの対象。**誤検知が解消**し、`GpuAvailable = false` によって
  REQ-GPU-02 の強制 OFF・チェックボックス無効化が正しく発火するようになった
- 2 行目のとおり、GPU 使用 OFF でも `GpuAvailable` は `true` のまま保たれる
  （`backends` は `UseGpu` に依存しないため）。チェックボックスが不用意に無効化されない
