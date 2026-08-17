# T119 — 「文字起こしにGPUを使用する」を OFF にしても GPU で動き続ける

> **状態:** 完了 — 2026-08-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **発端:** [T117](./T117-stop-session-crash.md) §3「やらないこと」で切り出した追加調査

## 1. 目的

GPU 使用チェックボックスを OFF にしても `RuntimeOptions.LoadedLibrary` が `Vulkan` のままで、
実際に CPU で動いているのか判別できない。疑い（ネイティブランタイムのプロセス単位ロード）を
実測で確定させ、正しい切り替え手段を決める。

---

## 2. 調査結果

### 2.1 結論

**疑いは正しい。切り替えは完全に無効であり、OFF にしても推論は GPU で走り続ける。**
原因は 2 つあり、両方とも `RuntimeLibraryOrder` を使った現在の方式が成立しないことを示す。

| # | 事実 | 根拠 |
|---|---|---|
| 1 | Whisper.net はネイティブランタイムを**プロセスで 1 回しか読み込まない** | `WhisperFactory` の静的フィールド `Lazy<LoadResult> LibraryLoaded`（下記 2.2） |
| 2 | `RuntimeOptions.LoadedLibrary` は**「どのランタイム DLL を読んだか」であって「GPU で計算しているか」ではない** | 下記 2.3 の実測で 4 パターンすべて `Vulkan` と表示された |

### 2.2 根拠 1 — ランタイムはプロセスで 1 回しかロードされない

`Whisper.net.dll` 1.9.0 をリフレクションで確認した（`lib/netstandard2.0/Whisper.net.dll`）。

```
=== Whisper.net.WhisperFactory ===
-- fields --
  Lazy`1 LibraryLoaded (static=True, init=True)   ← ここ
```

`WhisperFactory.CheckLibraryLoaded()` はこの `static readonly Lazy<LoadResult>` を参照する。
`Lazy<T>` は**初回アクセス時に 1 度だけ**ファクトリを走らせて結果をキャッシュするため、
`NativeLibraryLoader.LoadNativeLibrary()` はプロセス内で 1 回しか実行されない。

`RuntimeOptions.RuntimeLibraryOrder` はその 1 回の読み込み中にしか参照されない。
つまり [TranscriptionService.cs:136-139](../../AudioCaptureApp/Services/TranscriptionService.cs#L136-L139) の

```csharp
_factory.Dispose();
RuntimeOptions.RuntimeLibraryOrder = CpuOnlyOrder;
_factory = WhisperFactory.FromPath(modelPath);   // ← 何も読み込み直さない
```

は**丸ごと空振り**する。`_factory.Dispose()` はマネージド側のコンテキストを捨てるだけで、
ロード済みネイティブライブラリはアンロードされない。

### 2.3 根拠 2 — 実測（同一プロセス内・同一入力・同一モデル）

`ggml-small.bin` に 10 秒の合成音を与え、4 パターンを 1 プロセス内で順に実行した
（`RuntimeOptions.LoadedLibrary` の表示と所要時間）。

| # | 条件 | `LoadedLibrary` | 所要 |
|---|---|---|---|
| 1 | `RuntimeLibraryOrder = GPU 優先`（アプリの 1 回目） | `Vulkan` | **2423 ms** |
| 2 | `RuntimeLibraryOrder = CPU 限定` に変えて再読み込み（**アプリの 2 回目＝現在の実装**） | `Vulkan` | **2311 ms** |
| 3 | 順序は GPU 優先のまま `WhisperFactoryOptions.UseGpu = false` | `Vulkan` | **7421 ms** |
| 4 | 同上で `UseGpu = true`（対照） | `Vulkan` | **2472 ms** |

読み取れること。

- **#2 が #1 と同じ速度** → CPU 限定での再読み込みは効いておらず、**GPU で走り続けている**。
  これが報告された不具合そのもの。
- **#3 だけが約 3 倍遅い（7421 ms）** → `WhisperFactoryOptions.UseGpu = false` は**実際に効く**。
  T117 で実測した CPU 時の 1 チャンクあたり約 7.6 秒とも一致する。
- **4 パターンすべて `LoadedLibrary = Vulkan`** → この値は GPU 実行の有無を表さない。
  `GetRuntimeInfo()` も 4 パターンで同一文字列を返した（GPU 情報を含まない）。

### 2.4 正しい切り替え手段

`WhisperFactory.FromPath(string path, WhisperFactoryOptions options)` の
`WhisperFactoryOptions.UseGpu`（既定 `true`）を使う。これは whisper.cpp の
`whisper_context_params.use_gpu` に対応し、**ファクトリ単位**の設定なので何度でも切り替えられる。

```
=== Whisper.net.WhisperFactoryOptions === (既定値)
  UseGpu = True, UseFlashAttention = False, GpuDevice = 0, DelayInitialization = False, ...
```

ランタイム DLL は Vulkan 版のまま（＝`LoadedLibrary` は `Vulkan` のまま）でよい。
GPU 版バイナリを読み込んだうえで計算を CPU バックエンドに寄せる形になる。

---

## 3. スコープ境界

**やること**

- `LoadModel` を `WhisperFactoryOptions.UseGpu` 方式へ差し替え、二重読み込みを廃止する
- 不要になった `CpuOnlyOrder` を削除する
- `RuntimeInfo` の通知内容に「GPU 実行か CPU 実行か」を含める（現状の `Vulkan` だけでは判別不能）
- 上記に合わせて仕様書（§5）を先に直す

**やらないこと（重要）**

- **`GpuAvailable` の判定方法そのものの見直し。** §8 の残課題として起票する（下記 D3）
- **`LoadModel` がモデル読み込みの失敗を返せない件。** 調査中に見つけた別の不具合 → **別タスクで起票**（§8）
- **CUDA 経路の検証。** 本機は `IsRuntimeSupported(Cuda, win, x64) = False`（NVIDIA 環境なし）のため実測不能
- **新規パッケージの追加。** 既存の Whisper.net 1.9.0 の API だけで足りる

## 4. 決定事項

| # | 決定が必要だった点 | 結論 |
|---|---|---|
| D1 | CPU 限定にする手段 | **`WhisperFactoryOptions.UseGpu`**。`RuntimeLibraryOrder` はプロセス初回のみ有効なため使えない（§2.2 / §2.3 で実証） |
| D2 | GPU 可否判定のための「1 回目の GPU 優先読み込み」を残すか | **残す。ただし読み込みは 1 回だけにする。** `RuntimeLibraryOrder = GpuPreferredOrder` はプロセス初回に効き、以降は無害な代入になる |
| D3 | `GpuAvailable` を `IsGpuLibrary(LoadedLibrary)` のままにするか | **本タスクでは変えない。** §8 の残課題として T123 に起票する |
| D4 | `RuntimeInfo` の通知文言 | **`GPU (Vulkan)` / `CPU`**（2026-08-18 承認）。CPU 実行時はランタイム種別を表示しない |

## 5. 仕様書への影響

現行仕様は「CPU 限定順で読み込み直す」と書いており、**実装が仕様どおりでも動かない**。
仕様の方が誤り（実現不能な手段を規定している）なので、実装前に仕様を直す。

- [ ] `docs/spec/01_requirements.md`
  - **REQ-TRX-03** 改訂 — 「CPU 限定順で読み込み直す」→
    「`WhisperFactoryOptions.UseGpu = false` でファクトリを生成する」
  - **REQ-TRX-02** 微修正 — 「GPU 優先順での読み込み試行」は**プロセス内 1 回限り**である旨を明記
  - **REQ-GPU-03** 改訂 — 「現在のモデルを破棄して新しい設定で再読み込みする」の
    「新しい設定」が `UseGpu` を指すことを明記
  - **REQ-GPU-05** 改訂 — 通知内容を **GPU/CPU 実行の別**にする（`GPU (Vulkan)` / `CPU`。D4）
- [ ] `docs/spec/04_sequence_diagram.md` — §7 の
  `alt requestGpu == false かつ GPU利用可能と判定 / 破棄してCPU限定順で再読み込み` を削除し、
  単一の `FromPath(modelPath, UseGpu = requestGpu)` に置き換える
- [-] `docs/spec/02_architecture.md` — 影響なし（層構成・スレッドモデル不変）
- [-] `docs/spec/03_class_diagram.md` — 影響なし（`LoadModel(string, bool)` の公開シグネチャは不変）

## 6. アーキテクチャへの影響

なし。`TranscriptionService` 内部のモデル読み込み手順のみ。層構成・依存方向・スレッドモデル不変。

- ADR: **不要**

## 7. 変更ファイル一覧（実装時）

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `LoadModel` を単一読み込み＋`WhisperFactoryOptions.UseGpu` へ。`CpuOnlyOrder` 削除。通知文言を組み立てる `DescribeRuntime` を追加 |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `DescribeRuntime` のテスト 4 件（§8） |
| `docs/spec/01_requirements.md` | §5 のとおり（**実装より先**） |
| `docs/spec/04_sequence_diagram.md` | §5 のとおり（**実装より先**） |

### 実装後の `LoadModel` の骨子

```csharp
// RuntimeLibraryOrder が効くのはプロセス内で最初の 1 回だけ（Whisper.net の
// WhisperFactory.LibraryLoaded が static Lazy のため）。GPU/CPU の切り替えは
// ランタイムの読み直しではなく WhisperFactoryOptions.UseGpu で行う。
RuntimeOptions.RuntimeLibraryOrder = GpuPreferredOrder;
_factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });

var loaded = RuntimeOptions.LoadedLibrary;
var gpuAvailable = IsGpuLibrary(loaded);
// D4: GPU 実行時のみランタイム種別を併記する（"GPU (Vulkan)" / "CPU"）
RuntimeInfo?.Invoke(useGpu && gpuAvailable ? $"GPU ({loaded})" : "CPU");
return (true, gpuAvailable);
```

## 8. テスト一覧

通知文言の組み立てを純粋関数 `TranscriptionService.DescribeRuntime(RuntimeLibrary?, bool)` として
切り出したので、そこだけはユニットテストで守る（`AudioCaptureApp.Tests/TranscriptionServiceTests.cs`）。

- **`DescribeRuntime_GpuRequestedAndGpuLibraryLoaded_ReportsGpuWithLibraryName`** —
  GPU 実行時は `GPU (Vulkan)` / `GPU (Cuda)` のようにランタイム種別を併記すること
- **`DescribeRuntime_GpuLibraryLoadedButGpuNotRequested_ReportsCpu`** —
  **本タスクの本体**。GPU 版ランタイムを読み込んだままでも `useGpu = false` なら `CPU` と通知すること
- **`DescribeRuntime_CpuLibraryLoaded_ReportsCpuRegardlessOfRequest`** —
  CPU ランタイムしか読めなかったときは GPU 要求の有無に関わらず `CPU`
- **`DescribeRuntime_NoLibraryLoaded_ReportsCpu`** — `LoadedLibrary` が `null` でも落ちない

> **テストで守れない範囲:** `UseGpu = false` が実際に計算を CPU へ寄せているかは所要時間でしか
> 判別できず、実モデルと実 GPU を要求するため自動テスト化しない。
> §2.3 と「実行結果」の実測プローブで担保する（T117 / T118 / T120 と同じやり方）。

### 別タスクとして起票すべきもの（本タスクでは直さない）

1. **`LoadModel` がモデル読み込みの失敗を返せない。**
   `WhisperFactory.FromPath` はコンテキスト生成を遅延させるため、壊れた／非対応のモデルでも
   `LoadModel` は `(true, ...)` を返し、UI は「モデル読み込み完了」と表示する。
   実際の失敗は後続の `CreateBuilder()` で `WhisperModelLoadException` として出る
   （調査中に存在しないパスで再現：`FromPath` は成功し `CreateBuilder()` で送出）。
2. **`GpuAvailable` が Windows x64 で常に真になりうる。**
   `NativeLibraryLoader.IsRuntimeSupported(Vulkan, "win", "x64")` は本機で `True` を返し、
   GPU デバイスの有無を見ていない。Vulkan ランタイムの DLL 読み込み自体が失敗すれば
   CPU へフォールバックするが、「Vulkan ローダーはあるが使える GPU が無い」環境では
   `LoadedLibrary = Vulkan` になり REQ-GPU-02 の強制 OFF が発火しない疑いがある。
   **本機では GPU があるため反証・実証とも不可**。要調査。

## 9. 未解決の質問

**すべて解決済み（2026-08-18 承認）。**

1. ~~`RuntimeInfo` の通知文言~~ → **`GPU (Vulkan)` / `CPU`** に決定（D4）
2. ~~§8 の 2 件を台帳へ起票してよいか~~ → **起票する**。**T122**（モデル読み込み失敗を返せない）と
   **T123**（`GpuAvailable` が常に真になりうる疑い）として登録済み

## 10. 前提

- Whisper.net を **1.9.0 から上げない**。上げると `LibraryLoaded` の実装が変わりうる
- `WhisperFactoryOptions.UseGpu = false` が CPU 実行になることは §2.3 の所要時間差
  （7421 ms 対 2472 ms、同一プロセス・同一入力）から判断している。
  内部バックエンドを直接観測したわけではない
- 検証環境: Windows 11 / x64 / Vulkan 利用可・CUDA 利用不可、モデル `ggml-small.bin`

---

## 実行結果 (2026-08-18)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes` : 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **75** 件成功 / **0** 件失敗 / **0** 件スキップ

> 計測は `develop` 由来の `fix-gpu-toggle-and-model-load` ブランチ上で、T119 / T122 / T123 を
> まとめた最終状態に対して行った（3 件を 1 コミットにしたため中間状態は存在しない）。

- 計画からの逸脱: **なし**（§8 は当初「ユニットテスト不可」としたが、通知文言を純粋関数
  `DescribeRuntime` に切り出せたためテスト 4 件を追加した。テスト範囲が広がる方向の差分）

### 修正後の実測プローブ

実アプリの `TranscriptionService.LoadModel` / `TranscribeFileAsync` を**そのまま**呼び、
1 プロセス内で ON → OFF → ON と切り替えた（10 秒の合成音 WAV、`ggml-small.bin`）。

| 操作 | `RuntimeInfo` 通知 | 文字起こし所要 |
|---|---|---|
| GPU ON（ウォームアップ） | `GPU (Vulkan)` | 2955 ms |
| GPU ON | `GPU (Vulkan)` | **2363 ms** |
| **GPU OFF** | **`CPU`** | **7336 ms** |
| GPU ON へ戻す | `GPU (Vulkan)` | **2251 ms** |

- OFF で **3.1 倍**遅くなり、ON へ戻すと元の速度に戻る → **切り替えが双方向で効いている**
- 修正前（§2.3 #2）は OFF でも 2311 ms のままだった
- 通知文言も実行先に追従している（REQ-GPU-05）
