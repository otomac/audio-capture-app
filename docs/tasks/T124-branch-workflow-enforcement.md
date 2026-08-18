# T124 — ブランチ運用の規範化と強制

> **状態:** 完了 — 2026-08-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

新しいタスクの着手手順に「`develop` の最新化 → `develop` から作業ブランチを作成」を、完了手順に
「品質ゲート G1/G2/G3 が全緑になってから commit / push / PR 作成」を組み込み、文書（influence）と
フック（enforcement）の両方で固定する。現状これらは慣行として守られているだけで、規範にも強制にも
書かれていない。

## 2. スコープ境界

**やること**
- `docs/harness/00-ways-of-working.md` の「横断ルール」に **ブランチ運用** 節を追加
- `docs/harness/10-workflow.md` に **S0（ブランチ準備）** と **S8（統合：commit/push/PR）** を追加し、
  全体フロー図と「よくある逸脱」表を更新
- `.claude/hooks/guard-source-edit.ps1` に保護ブランチ判定を追加し、`develop` / `main` / `master` 上の
  `.cs` / `.xaml` 編集を **deny** する
- `.claude/hooks/session-start-context.ps1` に現在ブランチと運用ルールの注入を追加
- `CLAUDE.md` と `docs/harness/README.md` の該当箇所を追随させる
- 3 つのフックの入出力エンコーディングを UTF-8 に固定する（下記 D4）

**やらないこと（重要）**
- **4 つの法の増減・改番はしない。** ブランチ運用は「横断ルール」に置く（D1）
- **アプリ本体（`AudioCaptureApp/`）のソースは 1 行も触らない**
- **既存ブランチの改名・整理、`main` への `develop` 取り込みはしない**
- **`protect-commands.ps1` のゲート対象コマンドは増やさない**（`commit` / `push` / `switch` /
  `gh pr create` は既に ask 済み）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 規範のどこに置くか | **`00-ways-of-working.md` の「横断ルール」に節を追加**。法は 4 つのまま（他文書の改番を避ける） |
| D2 | 保護ブランチ上の編集は ask か deny か | **deny**。「今回だけ」を許さない。feature ブランチを切れば通る |
| D3 | ブランチ名の規約 | **`feature-` / `fix-` / `maintenance-` の 3 接頭辞**（既存 18 ブランチの実績どおり） |
| D4 | フックのエンコーディング破損を本タスクで直すか | **直す**。stdout が cp932 で出力され日本語が文字化け（`—` は `?` に脱落）していた。**強制メッセージが読めなければ強制にならない**ため、本タスクの成立条件と見なす |
| D5 | PR の宛先 | **`develop`**。`main` へは `develop` からのみ入る（PR #16〜#19 の実績どおり） |
| D6 | 起票（台帳・タスク票）はどのブランチで行うか | **feature ブランチ**。S0 を S1 より前に置き、台帳更新も PR の差分に含める |

## 4. 仕様書への影響

**仕様非影響。** 理由: アプリの機能・層構成・公開 API を変えない。開発プロセス（ハーネス）のみの変更。

## 5. アーキテクチャへの影響

- ADR: **不要**。3 層構成・依存方向・スレッドモデルのいずれにも触れない。ハーネスの規範変更は
  `docs/harness/README.md` §5 の手順（起票 → 文書 → 強制設定）で扱う。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `docs/harness/00-ways-of-working.md` | 「横断ルール」に **ブランチ運用** 節を追加（手順・接頭辞表・なぜ・強制） |
| `docs/harness/10-workflow.md` | フロー図に S0 / S8 を追加、S0・S8 の本文追加、S7 からコミット記述を S8 へ移動、逸脱表に 2 行追加 |
| `docs/harness/README.md` | §2 の enforcement 表で `guard-source-edit.ps1` の役割を更新 |
| `CLAUDE.md` | 「不可逆な操作」の前に **ブランチ運用** 節を追加 |
| `.claude/hooks/guard-source-edit.ps1` | 保護ブランチ判定（deny）を追加、UTF-8 入出力を固定 |
| `.claude/hooks/session-start-context.ps1` | 現在ブランチとブランチ運用の注入、UTF-8 出力を固定 |
| `.claude/hooks/protect-commands.ps1` | UTF-8 入出力を固定。commit / push / PR 作成の確認文にゲート確認とPR 宛先を追記（**ゲート対象コマンドは増やさない**） |
| `docs/tasks/backlog.md` | T124 を起票 → 完了へ移動 |

## 7. 実装手順

### グループ A — 規範（influence）
- [x] **A1** `00-ways-of-working.md` に「ブランチ運用」節を追加
- [x] **A2** `10-workflow.md` に S0 / S8 を追加しフロー図と逸脱表を更新
- [x] **A3** `CLAUDE.md` と `docs/harness/README.md` を追随

### グループ B — 強制（enforcement）
- [x] **B1** `guard-source-edit.ps1` に保護ブランチ deny を追加
- [x] **B2** 3 フックの UTF-8 入出力を固定
- [x] **B3** `session-start-context.ps1` にブランチ情報を注入

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** フックの手動検証（下記 §8）

## 8. テスト一覧

`AudioCaptureApp.Tests` への追加は **なし**（変更対象はハーネス文書と PowerShell フックであり、
アプリのアセンブリに含まれない）。代わりにフックへ JSON を流し込む手動検証を行う。

- **保護ブランチ × `.cs`** — `develop` 上で `MainViewModel.cs` を編集 → `deny` が返り、理由に
  `git switch -c` の手順が含まれる
- **feature ブランチ × `.cs`（進行中タスクあり・spec 未変更）** — `ask`（仕様書先行の確認）が返る
- **保護ブランチ × `.md`** — 対象外なので素通し（exit 0、出力なし）
- **エンコーディング** — 返却 JSON の日本語が UTF-8 バイト列で出力される（cp932 化しない）

> **テストで守れない範囲:** フックが実際に Claude Code のパーミッション判定へ効いているかは
> 手動でしか確認できない。また `guard-source-edit.ps1` は Edit / Write / MultiEdit のみを見るため、
> `Bash` 経由（`sed -i` 等）のソース改変は素通しする。この穴は本タスクでは埋めない。

## 9. 未解決の質問

なし（D1〜D6 で確定）。

## 10. 前提

- `develop` が統合先ブランチであり、`main` はリリース系である（PR #16〜#19 の実績に基づく）
- リモートは `origin`（https://github.com/otomac/audio-capture-app.git）1 つだけ
- フックは `pwsh`（PowerShell 7+）で実行される

---

## 実行結果 (2026-08-18)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 75 件成功 / 0 件失敗 / 0 件スキップ
- フックの手動検証（`pwsh` に JSON を流し込み）:

  | # | 条件 | 期待 | 結果 |
  |---|---|---|---|
  | 1 | `develop` × `AudioCaptureApp/ViewModels/MainViewModel.cs` | deny | **deny**（理由に `git switch -c` 手順を含む） |
  | 2 | `develop` × `docs/tasks/backlog.md` | 素通し | **exit 0・出力なし** |
  | 3 | `develop` × `obj/Debug/App.g.cs` | 素通し | **exit 0・出力なし** |
  | 4 | `feature-x` × 本体 `.cs`（`[~]` あり・spec 未変更） | ask（仕様書先行） | **ask 1 件** |
  | 5 | `feature-x` × `AudioCaptureApp.Tests/*.cs` | 素通し | **exit 0・出力なし** |
  | 6 | `feature-x` × 本体 `.cs`（`[~]` なし） | ask（タスク先行＋仕様書先行） | **ask 2 件** |

  | # | 条件 | 期待 | 結果 |
  |---|---|---|---|
  | 7 | `protect-commands.ps1` × `git commit` | ask ＋ ゲート確認の注記 | **ask**（「G1/G2/G3 が全て緑…PR の宛先は develop」を含む） |
  | 8 | `protect-commands.ps1` × `dotnet build` | 素通し | **exit 0・出力なし** |

- エンコーディング: 修正前は stdout が cp932 で出力され、日本語が文字化け・`—` が `?` に脱落していた
  （SessionStart の注入内容で確認）。修正後は UTF-8 バイト列で出力されることを確認。
- 計画からの逸脱: **本タスク自体は `develop` 上で実施した**（ルール制定前であり、途中で
  ブランチを切ると承認済みでない `git switch` が必要になるため）。次タスクから S0 が適用される。
