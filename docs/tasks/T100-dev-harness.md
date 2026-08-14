# T100 — 開発ハーネスの整備

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

「タスク先行 / 仕様書先行 / アーキテクチャ優先 / 品質ゲート」の 4 つを、
毎回思い出さなくても機能する形（規範文書＋強制フック＋ビルドゲート）に固定する。
`C:\personal\dev\dotnet-harness-main`（.NET 10 Web API + Next.js モノレポ向け）を参考にしつつ、
.NET 8 / WPF 単体デスクトップという本プロジェクトの前提に合わせて再設計する。

## 2. スコープ境界

**やったこと**
- `docs/harness/` に規範文書 9 本＋テンプレート 3 本を作成
- `docs/tasks/` `docs/adr/` `docs/archive/` を新設
- `Directory.Build.props` / `Directory.Packages.props` を新設し、両 `.csproj` を CPM 化
- `.editorconfig` に命名ルールとアナライザー severity を追加
- `.claude/hooks/` に強制フック 3 本＋ `settings.json` を整備
- `CLAUDE.md` を全面改訂
- CI ワークフローの `.sln` → `.slnx` 修正と G2（format 検証）ステップ追加

**やらなかったこと（重要）**
- **アプリのソースコード（`AudioCaptureApp/**/*.cs`, `*.xaml`）の変更。**
  静的解析の既存違反はソース修正せず、期限付き緩和＋解消タスク（T101〜T105）の起票に留めた。
- **Serena MCP の導入。** 利用不要との指示のため、参考ハーネスの Serena 強制フックと
  LSP 診断ゲートは採用せず、`dotnet format --verify-no-changes`（G2）で代替した。
- **Meziantou.Analyzer / SonarAnalyzer.CSharp の導入。** 既存コードへの影響が大きいため見送り。
  `Directory.Packages.props` にコメントで導入手順を残した。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 仕様書の正をどこに置くか | **`docs/spec/` に一本化。** 旧 `.spec/`（spec-kit 形式）は `docs/archive/initial-spec-kit/` へ退避し参照専用にした |
| D2 | 静的解析の強度 | **厳格＋実務チューニング。** `AnalysisMode=All` + 警告のエラー化を有効にし、WPF に不適なルールは理由付きで恒久緩和、既存違反は期限付き緩和＋解消タスク化 |
| D3 | 強制レイヤーの置き場 | **`docs/harness/`（規範）と `.claude/`（強制）の両方。** 文書だけでは全て「お願いベース」になるため |
| D4 | タスク台帳の置き場 | **`docs/tasks/`。** 様式は `docs/harness/60-task-format.md`、実データは `docs/tasks/` という分離 |
| D5 | Serena 非使用時の命名ゲート | **G2（`dotnet format --verify-no-changes`）が担う。** 実測で `dotnet build` は IDE1006 を報告しないが `dotnet format` は報告することを確認（§実行結果の注記） |
| D6 | `Async` サフィックス命名ルールの severity | **`suggestion`（レビュー担保）。** ルールが戻り値型を判別できず、WPF の `async void` イベントハンドラーを誤検出するため |
| D7 | CPM 導入の可否 | **導入する。** テストプロジェクトが本体より古いバージョン（xunit 2.5.3 / Test.Sdk 17.8.0 等）を参照しており、バージョンドリフトが起きていた |

## 4. 仕様書への影響

**仕様非影響。** 理由: アプリの機能・振る舞い・クラス構成を一切変更していない
（ビルド設定・ドキュメント・エージェント設定のみ）。
`docs/spec/02_architecture.md` の記述は現状のまま正しい。

## 5. アーキテクチャへの影響

構造そのものは変更していないが、**既存の暗黙の決定を明文化** するため ADR を 1 本起票した。

- [ADR-0001 ベースラインアーキテクチャ（3 層 / DI なし / 抽象化なし）](../adr/0001-baseline-architecture.md) — 承認済み

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `docs/harness/README.md` | 新規。ハーネスの入口、influence↔enforcement 対応表 |
| `docs/harness/00-ways-of-working.md` | 新規。4 つの法の本文 |
| `docs/harness/10-workflow.md` | 新規。S1〜S7 の作業手順 |
| `docs/harness/20-architecture-standards.md` | 新規。3 層・依存方向・スレッド・破壊の兆候 |
| `docs/harness/30-coding-standards.md` | 新規。C# 12 / .NET 8 標準 |
| `docs/harness/40-quality-gates.md` | 新規。G1/G2/G3、抑止の書き方、既知の穴、負債一覧 |
| `docs/harness/50-spec-standards.md` | 新規。`docs/spec/` の章立てと更新義務 |
| `docs/harness/60-task-format.md` | 新規。タスクの様式・状態遷移・完了条件 |
| `docs/harness/70-build-configuration.md` | 新規。ビルド構成ファイルの配置根拠 |
| `docs/harness/templates/{task,adr,spec-change}.md` | 新規。3 種のテンプレート |
| `docs/tasks/backlog.md` | 新規。タスク台帳 |
| `docs/adr/README.md`, `0001-baseline-architecture.md` | 新規 |
| `docs/archive/README.md` | 新規。`initial-spec-kit/` の位置づけ |
| `.spec/` → `docs/archive/initial-spec-kit/` | `git mv` で退避 |
| `Directory.Build.props` | 新規。`AnalysisMode=All` / `EnforceCodeStyleInBuild` / 警告のエラー化 |
| `Directory.Packages.props` | 新規。CPM |
| `AudioCaptureApp/AudioCaptureApp.csproj` | CPM 化（`Version` 属性削除）、重複プロパティ削除 |
| `AudioCaptureApp.Tests/AudioCaptureApp.Tests.csproj` | 同上 |
| `.editorconfig` | 名前空間・プライマリコンストラクター設定の是正、命名ルール追加、severity セクション追加、テスト緩和 |
| `.claude/hooks/guard-source-edit.ps1` | 新規。タスク先行・仕様書先行の強制 |
| `.claude/hooks/protect-commands.ps1` | 新規。破壊的コマンドと git 操作のゲート |
| `.claude/hooks/session-start-context.ps1` | 新規。法と進行中タスクの注入 |
| `.claude/settings.json` | フック配線、permissions（allow / secrets の deny） |
| `CLAUDE.md` | 全面改訂。ハーネス参照、古いパスの是正 |
| `.github/workflows/build-desktop.yml` | `.sln` → `.slnx`、SDK 9 追加、G2 ステップ追加、CI 側のフラグ上書き削除 |
| `AudioCaptureApp.Tests/*.cs`（4 ファイル） | `dotnet format` による改行コード正規化のみ（LF → CRLF）。ロジック変更なし |

## 7. テスト一覧

**追加なし。** 本タスクはビルド設定とドキュメントの整備であり、
アプリのロジックを変更していないため、新規のユニットテストは書いていない。

代わりに **フックの動作を手動で検証** した（§実行結果）。

> **テストで守れない範囲:** フック 3 本は xUnit の対象外（PowerShell スクリプトのため）。
> 動作確認は手動のペイロード投入で行っている。CI では実行されない。

## 8. 前提

- 開発機・CI ともに `pwsh`（PowerShell 7+）が PATH にあること。フックはすべて `pwsh` 前提。
  フックはフェイルオープン設計のため、`pwsh` が無い環境でも作業は止まらない（強制が効かなくなるだけ）。
- .NET SDK 9 以降がインストールされていること（`.slnx` の読み込みに必要）。
  ローカル実測環境は SDK 10.0.111。

---

## 実行結果 (2026-08-14)

- `dotnet build AudioCaptureApp.slnx -c Debug` : **0 個の警告 / 0 エラー**
- `dotnet format AudioCaptureApp.slnx --verify-no-changes` : **差分なし（終了コード 0）**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **26 件合格 / 0 件失敗 / 0 件スキップ / 合計 26 件**

### フックの手動検証（7 ケース、すべて期待どおり）

| ケース | 期待 | 結果 |
|---|---|---|
| `.cs` 編集・進行中タスクなし・仕様書未変更 | ask（2 件の理由） | ✅ |
| `README.md` 編集 | 素通し | ✅ |
| `obj/` 配下の生成 `.cs` 編集 | 素通し | ✅ |
| `git commit -m test` | ask | ✅ |
| ルートの再帰削除 | deny | ✅ |
| `dotnet build AudioCaptureApp.slnx` | 素通し | ✅ |
| `git status --short` | 素通し | ✅ |

### 実装中に判明した事実（D5 の根拠）

**`dotnet build`（`EnforceCodeStyleInBuild=true` + 警告のエラー化）は IDE1006（命名）を報告しないが、
`dotnet format` は報告する。** `.editorconfig` に命名ルールを追加した直後のビルドは 0 警告 / 0 エラー
だったのに対し、同じ状態で `dotnet format --verify-no-changes` を実行すると IDE1006 が
5 ファイル（`SettingsService` / `TranscriptionService` / `LevelMeterControl` / `MainViewModel` /
`MainWindow`）で検出された。
参考ハーネスは Serena（Roslyn LSP）でこの穴を埋めていたが、本プロジェクトでは **G2 が同じ役割を果たす**。
これが 3 つのゲートのうち G2 を必須にしている理由である。

### 計画からの逸脱

- 命名ルールの初版が広すぎ、`private static readonly` / `const`（PascalCase が正しい）と
  WPF の `async void` イベントハンドラーを誤検出した。
  前者は const / static readonly 用のルールを先に置いて解消、後者は D6 のとおり `suggestion` に変更した。
- `.editorconfig` の `csharp_style_namespace_declarations` が `block_scoped` だったが、
  実コードは全てファイルスコープ名前空間だった。設定を実態（`file_scoped`）に合わせた。
- テストプロジェクトの 4 ファイルが LF 改行だったため、`dotnet format` で CRLF に正規化した
  （`.editorconfig` の `end_of_line = crlf` に従った空白のみの変更）。
