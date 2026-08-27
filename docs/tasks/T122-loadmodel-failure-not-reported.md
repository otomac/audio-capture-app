# T122 — `LoadModel` がモデル読み込みの失敗を返せない

> **状態:** 完了 — 2026-08-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **発端:** [T119](./T119-gpu-toggle-ineffective.md) §8 で切り出した別件

## 1. 目的

壊れた／非対応の Whisper モデルを指定しても `LoadModel` が成功を返し、UI が
「モデル読み込み完了」と表示してしまう。実際の失敗は録音開始やファイル文字起こしまで
表面化しない。読み込み時点で失敗を検出して返せるようにする。

---

## 2. 調査結果

### 2.1 `FromPath` はネイティブ側の失敗を握り潰す

`WhisperFactoryOptions.DelayInitialization` の既定は `false` で、**`FromPath` の時点で
ネイティブのモデル読み込みは実行されている**（ログがその場で出る）。
にもかかわらず、**失敗しても `FromPath` は例外を投げず、`WhisperFactory` を返す。**

`LogProvider.AddLogger` でネイティブログを拾って確認した実測（Whisper.net 1.9.0）。

| 入力 | `FromPath` | `CreateBuilder()` | ネイティブログ |
|---|---|---|---|
| 正常なモデル | 成功 | 成功 | `whisper_model_load: model size = 487.01 MB` |
| 存在しないパス | **成功（例外なし）** | `WhisperModelLoadException` | `failed to open '...'` |
| 壊れたファイル（ランダム 1MB） | **成功（例外なし）** | `WhisperModelLoadException` | `invalid model data (bad magic)` |

`DelayInitialization` を `true` / `false` のどちらに明示しても結果は同じで、
**失敗が例外として表面化するのは最初の `CreateBuilder()`**（`WhisperFactory.contextLazy` の
評価結果を確認する箇所）だった。

### 2.2 現在のアプリで起きること

[TranscriptionService.LoadModel](../../AudioCaptureApp/Services/TranscriptionService.cs) は
`File.Exists` しか見ていないため、壊れたモデルでも `(true, ...)` を返す。
`MainViewModel.TryLoadWhisperModel` は `TranscriptionStatus = "モデル読み込み完了"` を表示し、
`AudioCaptureService` にサービスをワイヤしてしまう。
実際の `WhisperModelLoadException` は後続の `RegisterSource`（録音開始時）や
`TranscribeFileCoreAsync`（ファイル文字起こし時）の `CreateBuilder()` で初めて出る。

### 2.3 検出手段とそのコスト

`CreateBuilder()` を 1 度呼べば失敗が確定する。`FromPath` の時点でコンテキストは
読み込み済みなので**追加コストは無い**。実測（正常モデル）:

```
1 回目 CreateBuilder(): 0 ms
2 回目 CreateBuilder(): 0 ms
```

`WhisperProcessorBuilder` は `IDisposable` を実装しないため、戻り値を捨ててよい。

## 3. スコープ境界

**やること**

- `LoadModel` で `FromPath` の直後に `CreateBuilder()` を 1 度呼び、失敗を確定させる
- 失敗時は既存の `catch` 経路（`Error` イベント通知＋`(false, ...)` 返却）に載せる

**やらないこと（重要）**

- **`MainViewModel` 側の表示・分岐。** `LoadModel` が `false` を返せば既存の
  `else` 経路（「モデル読み込み失敗」表示＋`SetTranscriptionService(null)`）がそのまま動く
- **`GpuAvailable` の判定方法。** → **T123**
- **`LoadModel` の公開シグネチャ変更。** 失敗理由の型付けは行わない（`Error` イベントの文字列で足りる）
- **`RegisterSource` / `TranscribeFileCoreAsync` 側の例外処理。** 既存のまま

## 4. 決定事項

| # | 決定が必要だった点 | 結論 |
|---|---|---|
| D1 | 失敗を確定させる手段 | **`_factory.CreateBuilder()` を 1 度呼ぶ。** `DelayInitialization` を変えても `FromPath` は例外を投げないため、これ以外に手段が無い（§2.1） |
| D2 | 検証を `LoadModel` に置くか呼び出し側に置くか | **`LoadModel` 内。** 「読み込めたかどうか」を返すのが `LoadModel` の責務であり、呼び出し側は既に `success` で分岐している |
| D3 | 失敗時の `GpuAvailable` 戻り値 | **`true` のまま（既存踏襲）。** 判定不可のときは GPU 利用可能とみなしてチェックボックスを操作可能に保つ |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — **REQ-TRX-01** 改訂。「存在しないパスの場合は失敗を返す」→
  「存在しないパス、および Whisper モデルとして読み込めないファイルの場合は失敗を返す」
- [x] `docs/spec/04_sequence_diagram.md` — §7 に読み込み検証のステップを追加
- [-] `docs/spec/02_architecture.md` — 影響なし
- [-] `docs/spec/03_class_diagram.md` — 影響なし（`LoadModel(string, bool)` のシグネチャ不変）

## 6. アーキテクチャへの影響

なし。`TranscriptionService.LoadModel` 内の 1 行追加。ADR: **不要**

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `FromPath` の直後に `CreateBuilder()` を呼んで読み込み失敗を確定させる |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 壊れたモデルで `LoadModel` が失敗を返すテスト |
| `docs/spec/01_requirements.md` | §5 のとおり（**実装より先**） |
| `docs/spec/04_sequence_diagram.md` | §5 のとおり（**実装より先**） |

## 8. テスト一覧

- **`LoadModel_CorruptModelFile_ReturnsFailureAndRaisesError`** —
  Whisper モデルとして不正なファイルを渡すと `LoadModel` が `success = false` を返し、
  `IsModelLoaded` が `false` のままで、`Error` イベントが発火すること

> **前提:** このテストは Whisper のネイティブランタイム（`Whisper.net.Runtime*` パッケージが
> 出力へ配置する DLL）のロードを伴う。実 GGML モデルや実オーディオデバイスは要求しない
> （不正データを書いた一時ファイルを使う）。

> **テストで守れない範囲:** 「正常なモデルなら成功を返す」側は実 GGML モデル（数百 MB）を
> 要求するためテスト化しない。実測プローブで担保する。

## 9. 未解決の質問

なし。

## 10. 前提

- Whisper.net を **1.9.0 から上げない**。`FromPath` が失敗時に例外を投げるようになれば
  `CreateBuilder()` の呼び出しは冗長になる（害は無い）
- 検証環境: Windows 11 / x64 / Vulkan 利用可・CUDA 利用不可

---

## 実行結果 (2026-08-18)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes` : 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **75** 件成功 / **0** 件失敗 / **0** 件スキップ

> 計測は `develop` 由来の `fix-gpu-toggle-and-model-load` ブランチ上で、T119 / T122 / T123 を
> まとめた最終状態に対して行った（3 件を 1 コミットにしたため中間状態は存在しない）。

- 計画からの逸脱: **なし**

### テストが回帰を捕まえることの確認

`CreateBuilder()` の 1 行を一時的にコメントアウトして同テストを実行し、
**修正前は落ちる**ことを確認した（確認後に復元済み）。

```
LoadModel_CorruptModelFile_ReturnsFailureAndRaisesError [FAIL]
  Assert.False() Failure
  Expected: False
  Actual:   True
```

### 補足 — 分析器による計画変更

テストの不正データ生成に `Random.Shared.NextBytes` を使ったところ **CA5394**
（安全でない乱数ジェネレーター）でビルドが落ちた。抑制はせず、固定パターン
（`0xAB` 埋め）に変更して解消した。GGML のマジック `0x67676d6c` と一致しなければ
目的は達せられるため、テストの意図は変わらない。
