# T155 — `MainViewModel` を分割するかを ADR で決める

> **状態:** 完了 — 2026-08-27
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **成果物:** [ADR-0005](../adr/0005-mainviewmodel-split.md)

## 1. 目的

[T154](./T154-ui-refresh.md)（UI の改良）が **4 枚目のウィンドウ `SettingsWindow`** を足すため、
[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md) が自ら書いた再評価の条項

> ウィンドウが 4 枚目・5 枚目と増えるなら、この ADR を置換して案 A を選び直すことになる。

に触れた。**`MainViewModel` を分割するかどうかを決め、記録する。**
T154 はこの結論が出るまで中断している。

## 2. スコープ境界

**やること**

- 現状の実測（行数・バインドの重なり・共有される状態の書き手）
- 選択肢の比較と ADR の起草（[ADR-0005](../adr/0005-mainviewmodel-split.md)）
- 利用者の承認を得ること
- **承認された決定に規範側を揃えること**（`CLAUDE.md` / `20-architecture-standards.md`）。
  ADR とアーキテクチャ規範を乖離させない（[templates/adr.md](../harness/templates/adr.md) の末尾）
- 実装タスクの起票

**やらないこと（重要）**

- **`.cs` を 1 行も変更しない。** 本タスクの成果物は ADR だけである。
- **分割そのものを実施しない。** 承認された案の実装は**別タスク**として起票する。
- **T154 の画面設計（D1〜D13）を蒸し返さない。** 本タスクで決まるのは
  「どの ViewModel を `DataContext` にするか」だけであり、見た目とレイアウトには影響しない。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ADR を書くか | **書く。** ADR-0002 の条項に触れた以上、根拠を残さずに 4 枚目を足さない（法 3） |
| D2 | ADR-0002 の扱い | **書き換えない。** 決定を覆すときは新しい ADR を書く（[adr/README.md](../adr/README.md) の使い方 4） |
| D3 | 判断の材料 | **推測ではなく実測に基づく。** 行数・各ウィンドウのバインド集合・共有状態の書き手を数える |
| D4 | 分割の実施 | **本タスクでは行わない。** 承認後に別タスクへ送る |

## 4. 実測（2026-08-26）

| 項目 | ADR-0002 記述時 | 現在 |
|---|---|---|
| `MainViewModel.cs` の行数 | 788 | **1,396**（コード 922 / ドキュメントコメント 214 / 行コメント 75 / 空行 185） |
| 分割の閾値 | 1,500 | 1,500 |
| ウィンドウ枚数 | 3 | 3（T154 で 4） |

各ウィンドウがバインドしているプロパティは **3 集合とも完全に素**（`MainWindow` 33 /
`FileTranscriptionOptionsWindow` 11 / `LiveTranscriptWindow` 1、重複 0）。
一方で **`IsNotBusy` / `StatusMessage` / `LastResultPath` / `LiveTranscriptLines` は
書き手が画面をまたいでいる。** 詳細は ADR-0005 の「現状」。

## 5. 仕様書への影響

- **なし。** 本タスクは `docs/adr/` と `docs/tasks/` にしか触れない。
  承認された案に応じた `docs/spec/` の更新は、ADR-0005 の「追随して更新するもの」に列挙してあり、
  **実施は分割の実装タスク**（または T154）で行う。

## 6. アーキテクチャへの影響

本タスクそのものが ADR である。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `docs/adr/0005-mainviewmodel-split.md`（新規） | 本 ADR |
| `docs/adr/README.md` | 一覧に ADR-0005 を追加 |
| `docs/adr/0002-secondary-windows-share-mainviewmodel.md` | 冒頭に ADR-0005 への前方参照を 1 行追加（**決定本文は書き換えない**） |
| `CLAUDE.md` | 「ViewModel は `MainViewModel.cs` 1 ファイルに集約」→「`MainViewModel` **1 クラス**に集約。ファイルは `partial` で機能単位に割ってよい」 |
| `docs/harness/20-architecture-standards.md` | §1 の ViewModel の場所、§4「意図的にやっていないこと」、§5「新しいウィンドウ」、§6 の閾値（**全ファイル合計**）と再評価契機② |
| `docs/tasks/T155-viewmodel-split-adr.md`（新規） | 本ファイル |
| `docs/tasks/T156-mainviewmodel-partial-split.md`（新規） | 実装タスクの起票 |
| `docs/tasks/backlog.md` | T155 / T156 の起票 |

## 8. 実装手順

- [x] **A1** 実測を取る（行数・バインド集合・共有状態の書き手）
- [x] **A2** ADR-0005 を起草する
- [x] **A3** `docs/adr/README.md` の一覧へ追加する
- [x] **A4** 利用者の承認を得て、状態を「承認済み」にする（**案 D を承認・2026-08-27**）
- [x] **A5** 実装タスク **[T156](./T156-mainviewmodel-partial-split.md)** を起票する
- [x] **A6** 規範側を ADR に揃える（`CLAUDE.md` / `20-architecture-standards.md` §5・§6 / ADR-0002 への前方参照）
- [x] **A7** T154 を再開できる状態にする → **REQ-SETWIN-04 は書いたとおりで確定**。
      T154 のタスク票（§5）の書き換えは、そのファイルが `feature-ui-refresh` にしか無いため
      **T154 のブランチで行う**

## 9. テスト一覧

**なし。** ドキュメントのみのタスクであり、`.cs` を変更しない。
品質ゲート G1 / G2 / G3 は「変更していないことの確認」として実行する。

## 10. 未解決の質問

**なし。** 2026-08-27 に**案 D が承認された** — `partial` によるファイル分割を採り、
案 A（ウィンドウごとの ViewModel）は①`MainViewModel` の全ファイル合計が 1,500 行を超えたとき、
または②5 枚目のウィンドウを足すときに再評価する。

これにより **T154 の REQ-SETWIN-04 は書いたとおりで確定**した — `SettingsWindow` は
`MainViewModel` を共有する状態レス View とする（ADR-0002 の規則 1〜5 はすべて有効）。

---

## 実行結果 (2026-08-27)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : **241 件成功 / 0 件失敗 / 0 件スキップ**（追加・変更なし。本タスクは `.cs` を触っていない）
- 計画からの逸脱: **§2「やること」に 2 項目を後から足した** — 規範側（`CLAUDE.md` /
  `20-architecture-standards.md`）を ADR に揃えることと、実装タスクの起票。
  起票時は「成果物は ADR だけ」と書いていたが、[templates/adr.md](../harness/templates/adr.md) が
  末尾で「ADR とアーキテクチャ規範を乖離させないこと」を求めているため、同じタスクで直した。
- **当初「ADR 不要」と判断していたのは誤りだった。** T154 のタスク票にそう書いて進めかけたが、
  ADR-0002 が「結果」の節で 4 枚目を再評価の契機に指定しており、条項に触れている。
  利用者の指摘ではなく、ADR-0002 を読み直して気付いた。判断の記録として残す。
- 承認された案: **案 D**（`partial` によるファイル分割）。実装は
  [T156](./T156-mainviewmodel-partial-split.md)。
