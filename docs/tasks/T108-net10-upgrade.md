# T108 — 実行基盤を .NET 8 から .NET 10 へ引き上げる

> **状態:** 完了 — 2026-08-15
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

アプリケーション本体・テスト・CI を含むソフトウェア全体の実行基盤を .NET 8 から .NET 10 へ
引き上げ、以降の機能追加を .NET 10 のサポート期間内で行えるようにする。

## 2. スコープ境界

**やること**

- `TargetFramework` を `net8.0-windows` → `net10.0-windows`（本体・テストの両方）
- CI (`.github/workflows/build-desktop.yml`) の `DOTNET_VERSION` を `10.0.x` へ
- 実行基盤バージョンを記載している仕様書・ハーネス・README の追従

**やらないこと（重要）**

- **NuGet パッケージのバージョン更新。** TFM 引き上げと同時に動かすと、
  障害が出たときにどちらが原因か切り分けられなくなる。既存バージョンのまま .NET 10 で通す。
- **`LangVersion` の明示的な固定。** 既に `latest` であり、SDK に追従させる方針を変えない。
- **アプリケーションコードの書き換え。** C# 14 の新機能を使うための書き換えは行わない。
- **`AnalysisLevel` / `.editorconfig` の緩和。** 新 SDK で警告が増えたら緩和ではなく修正で対応する
  （実測の結果、増加はゼロだった）。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | TFM を `net10.0-windows` にするか `net10.0` + WPF にするか | **`net10.0-windows`。** WPF は Windows 固有 TFM が必須で、現行構成の素直な引き上げ |
| D2 | CI に 8.0.x を残すか | **残さない。** ターゲットが 1 つに絞られ、`.slnx` 要件（SDK 9 以降）も 10.0.x が満たす |
| D3 | パッケージ更新を同梱するか | **しない（§2 参照）。** 別タスクとして切り出す |
| D4 | ドキュメント上の言語表記 | **「C# 14 / .NET 10」。** `LangVersion=latest` のため SDK 10 では C# 14 が既定 |

## 4. 仕様書への影響

実行環境の記載が変わるため、以下 2 章が対象。要件・クラス構成・処理順序は変わらない。

- [x] `docs/spec/00_overview.md` — 実行環境を `.NET 10 (net10.0-windows)` へ
- [x] `docs/spec/02_architecture.md` — 技術スタック表の言語欄を `C# 14 / .NET 10` へ
- [-] `docs/spec/01_requirements.md` — 影響なし（機能要件は不変）
- [-] `docs/spec/03_class_diagram.md` — 影響なし（公開クラス／メソッドの増減なし）
- [-] `docs/spec/04_sequence_diagram.md` — 影響なし（処理順序の変更なし）

## 5. アーキテクチャへの影響

3 層構成・依存方向・スレッドモデルのいずれにも触れない。ランタイムの基準バージョン変更のみ。

- ADR: **不要**。[00-ways-of-working.md 法 3](../harness/00-ways-of-working.md#法-3--アーキテクチャ優先) が
  ADR を要求する変更（層の増減・依存方向・DI 導入・`MainViewModel` 分割・Service 抽象化・
  新規外部ライブラリ採用）のいずれにも該当しない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/AudioCaptureApp.csproj` | `TargetFramework` を `net10.0-windows` へ |
| `AudioCaptureApp.Tests/AudioCaptureApp.Tests.csproj` | 同上 |
| `Directory.Build.props` | TFM を記載したコメントの追従 |
| `.github/workflows/build-desktop.yml` | `DOTNET_VERSION` を `10.0.x` 単一指定へ |
| `docs/spec/00_overview.md` | 実行環境の記載 |
| `docs/spec/02_architecture.md` | 技術スタック表 |
| `docs/harness/30-coding-standards.md` | 見出し・ベースライン記載 |
| `docs/harness/70-build-configuration.md` | 配置図の TFM 注記、§6 の `DOTNET_VERSION` 注記 |
| `docs/harness/README.md` | 30 番文書の説明行 |
| `CLAUDE.md` | 技術スタックの言語行 |
| `README.md` | 動作要件の Runtime 行 |

## 7. 実装手順

### グループ A — TFM 引き上げ

- [x] **A1** 本体の `TargetFramework` を変更 (`AudioCaptureApp/AudioCaptureApp.csproj`)
- [x] **A2** テストの `TargetFramework` を変更 (`AudioCaptureApp.Tests/AudioCaptureApp.Tests.csproj`)
- [x] **A3** 旧 TFM の `obj/` `bin/` を削除する。
      WPF の一時プロジェクト（`*_wpftmp.csproj`）が旧 TFM のパスに残った `.g.cs` を
      参照し続け、`CS2001` で失敗するため（実際に発生した）

### グループ B — CI

- [x] **B1** `DOTNET_VERSION` を `10.0.x` 単一指定へ (`.github/workflows/build-desktop.yml`)
- [x] **B2** self-contained publish がローカルで通ることを確認する（CI と同じ引数）

### グループ C — ドキュメント追従

- [x] **C1** 仕様書 2 章を更新（§4）
- [x] **C2** ハーネス 3 文書＋`CLAUDE.md`＋`README.md` を更新

### グループ Z — 検証

- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

**新規テストは追加しない。** 本タスクは実行基盤の差し替えであり、振る舞いを変更しない。
既存 26 件が .NET 10 上でそのまま成功することをもって回帰の検証とする。

> **テストで守れない範囲:** WASAPI キャプチャ・LAME エンコード・Whisper ネイティブ呼び出しは
> ユニットテストの対象外（実機デバイスとモデルを要求するため）。.NET 10 でのこれらの動作は
> 手動確認に依存する。self-contained publish が成功し、Whisper の
> `runtimes/`（cuda / vulkan / win-x64）が出力に含まれることまでは確認済み。

## 9. 未解決の質問

なし。

## 10. 前提

- ビルド環境に .NET SDK 10 が入っていること（実測: `10.0.111`）。
- CI の `windows-latest` で `actions/setup-dotnet@v5` が `10.0.x` を解決できること。
  **これはローカルでは検証できない。** PR の CI 実行が初回の検証機会になる。
- 既存 NuGet パッケージ（NAudio 2.2.1 / Whisper.net 1.9.0 / CommunityToolkit.Mvvm 8.3.2 /
  xunit 2.9.3 など）が `net10.0-windows` で解決できること（実測: 復元・ビルドとも成功）。

---

## 実行結果 (2026-08-15)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **26** 件成功 / **0** 件失敗 / **0** 件スキップ
- `dotnet publish -c Release -r win-x64 --self-contained true` : 成功
  （`AudioCaptureApp.exe` 生成、約 422 MB、`runtimes/` に cuda・vulkan・win-x64 を含む）
- 計画からの逸脱: なし。ただし A3（`obj/` `bin/` の削除）は事前に想定しておらず、
  `CS2001` の発生を受けて手順へ追記した。
</content>
</invoke>
