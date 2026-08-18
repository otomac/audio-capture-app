# AudioCaptureApp 開発ハーネス

このディレクトリは、AudioCaptureApp の開発における **規範（ルール）の唯一の置き場** である。
人間の開発者にも、コーディングエージェント（Claude Code 等）にも、同じ規範が適用される。

> **ハーネスとは。** 「毎回思い出さないと守れないルール」を、文書・設定・フック・ビルド設定として
> 固定化したもの。規約は誰かの頭の中にある限り必ず風化するが、ルールファイルやアナライザーや
> フックに書かれた規約は風化しない。

---

## 1. 4 つの法

このプロジェクトの作業は、例外なく次の 4 つの法に従う。詳細は
[00-ways-of-working.md](./00-ways-of-working.md) にある。

| # | 法 | 一言でいうと |
|---|---|---|
| 1 | **タスク先行** | タスクを起票していない作業は存在しない。進捗はタスク台帳でのみ管理する。 |
| 2 | **仕様書先行** | 仕様が変わるなら、ソースより先に `docs/spec/` を直す。 |
| 3 | **アーキテクチャ優先** | 3 層構成と依存方向は都合で曲げない。曲げるなら ADR を書く。 |
| 4 | **品質ゲート** | 静的解析とユニットテストを両方クリアするまで「完了」と言わない。 |

---

## 2. influence と enforcement

ハーネスの構成要素は 2 種類ある。**この区別がハーネス全体の背骨** である。

- **influence（お願い）** — 文書やルール。だいたい守られるが、破ろうと思えば破れる。
- **enforcement（強制）** — フック・パーミッション・ビルド失敗。破れない。

危険な失敗は enforcement に置き、それ以外は influence に置く。

| 構成要素 | 役割 | 種別 | 実体 |
|---|---|---|---|
| `CLAUDE.md` | 常時ロードされるプロジェクトの法 | influence（強） | リポジトリルート |
| `docs/harness/*.md` | 規範の本体（この文書群） | influence | Markdown |
| `docs/harness/templates/` | タスク票・ADR・仕様変更の様式 | influence | Markdown |
| `.claude/hooks/guard-source-edit.ps1` | 保護ブランチ（`develop` / `main`）上のソース編集を **deny**。タスク未起票・仕様書未更新のソース編集を **ask** で止める | **enforcement** | PowerShell |
| `.claude/hooks/protect-commands.ps1` | 破壊的コマンドと git 操作を **deny / ask** する | **enforcement** | PowerShell |
| `.claude/hooks/session-start-context.ps1` | セッション開始時に法・現在ブランチ・進行中タスクを注入する | influence（自動） | PowerShell |
| `Directory.Build.props` | アナライザー全開＋警告をエラー化 | **enforcement** | MSBuild |
| `Directory.Packages.props` | NuGet バージョンの一元管理（CPM） | **enforcement** | MSBuild |
| `.editorconfig` | 書式・命名・ルール別 severity | **enforcement** | EditorConfig |
| `.github/workflows/build-desktop.yml` | CI での build / format / test ゲート | **enforcement** | GitHub Actions |

判断の指針：

- **こう書いてほしい** → 文書（ここ）に書く。
- **絶対にやらせない** → フックか `permissions.deny` にする。
- **コードがこうなっていてはいけない** → アナライザーを `warning` にする（ビルドが落ちる）。

---

## 3. 文書一覧

| ファイル | 内容 |
|---|---|
| [00-ways-of-working.md](./00-ways-of-working.md) | 4 つの法の本文。守るべきことの全体。 |
| [10-workflow.md](./10-workflow.md) | 起票 → 仕様 → 実装 → ゲート → 完了 の作業手順。 |
| [20-architecture-standards.md](./20-architecture-standards.md) | 3 層構成・依存方向・スレッドモデルの規範。 |
| [30-coding-standards.md](./30-coding-standards.md) | C# 14 / .NET 10 のコーディング標準。 |
| [40-quality-gates.md](./40-quality-gates.md) | 品質ゲートの定義・実行コマンド・既知の穴。 |
| [50-spec-standards.md](./50-spec-standards.md) | `docs/spec/` の章立てと更新義務。 |
| [60-task-format.md](./60-task-format.md) | タスク票の様式と状態遷移。 |
| [70-build-configuration.md](./70-build-configuration.md) | ビルド構成ファイルの配置と根拠。 |

## 4. 実データの置き場（ハーネス外）

規範は `docs/harness/` に、**実データはその外** に置く。この分離を崩さないこと。

| 置き場 | 中身 | 様式の定義元 |
|---|---|---|
| `docs/tasks/backlog.md` | タスク台帳（起票キュー・進行中・完了） | [60-task-format.md](./60-task-format.md) |
| `docs/tasks/<ID>-<slug>.md` | タスク詳細＝実装計画 | [templates/task.md](./templates/task.md) |
| `docs/spec/` | 現行仕様書（**唯一の正**） | [50-spec-standards.md](./50-spec-standards.md) |
| `docs/adr/` | アーキテクチャ決定記録 | [templates/adr.md](./templates/adr.md) |
| `docs/archive/` | 過去の成果物（`initial-spec-kit/` 等）。参照専用、更新しない。 | — |

## 5. 規範を変えたいとき

このハーネス自体も変更可能である。ただし **なし崩しに逸脱するのではなく、文書を直す**。

1. 変更を **タスクとして起票** する（ハーネス変更もタスク先行の対象）。
2. 該当する `docs/harness/*.md` を直す。
3. enforcement を伴う変更（フック・アナライザー severity）なら、対応する設定ファイルも同じ変更で直す。文書と設定を絶対に乖離させない。
4. アーキテクチャに関わる変更なら [templates/adr.md](./templates/adr.md) で ADR も残す。
