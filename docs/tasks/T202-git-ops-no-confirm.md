# T202 — git の commit / push / ブランチ作成を都度確認の対象から外す

> **状態:** 完了 — 2026-11-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

利用者の依頼 (2026-11-23): git の commit・push・ブランチ作成は確認不要にし、明示的な指示が要るのは
PR 作成だけにする。規範（文書）と強制（フック・権限設定）の両方を揃える。

## 2. スコープ境界

**やること**
- `git add` / `commit` / `push` / ブランチの作成・切替を、確認なしで行ってよい操作にする
- PR 作成（`gh pr create`）だけを「依頼されたときだけ行う」操作として残す
- 規範の文書（`CLAUDE.md`・`00-ways-of-working.md`・`10-workflow.md`）とフック 3 本・`settings.json` を揃える

**やらないこと（重要）**
- **破壊的な操作の確認は外さない。** force push・`git reset --hard`・`git clean -f`・`git checkout --`・
  ブランチの強制削除・ファイル削除は今までどおり `protect-commands.ps1` が `ask` で止める
- **品質ゲートの前提は変えない。** G1/G2/G3 が緑になる前に commit しない・途中まで push しない、は残す
- **ブランチ運用は変えない。** 1 タスク = 1 ブランチ、`develop` 起点、保護ブランチ上で `.cs` / `.xaml` を編集しない（`guard-source-edit.ps1` の deny）は残す

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 確認なしにする操作 | **`git add` / `commit`（`--amend` を含む）/ `push`（force を除く）/ `switch`（作成を含む）/ `pull` / `merge` / `rebase`。** 依頼は「指示が要るのは PR のみ」なので、merge / rebase も外す。push 済みの履歴を書き換えると force push が要り、そこで止まる |
| D2 | PR 作成 | **依頼されたときだけ行う。** `protect-commands.ps1` の `ask` を残し、品質ゲートと宛先 `develop` の注意書きもそこに残す |
| D3 | 権限設定 | **`settings.json` の `allow` に `git add` / `commit` / `push` / `switch` / `pull` を足す。** フックが `ask` を返さなくなるだけでは、権限モードによっては Claude Code 本体が確認を出すため。force push はフックの `ask` が `allow` より優先されるので止まる |

## 4. 仕様書への影響

仕様非影響。理由: 開発の進め方（ハーネス）の変更で、アプリの振る舞いは変わらない。

## 5. アーキテクチャへの影響

- ADR: 不要（層構成・依存方向・スレッドモデルに触れない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `CLAUDE.md` | 「不可逆な操作」を「PR 作成だけは依頼されたときだけ」に改める |
| `docs/harness/00-ways-of-working.md` | 「ブランチ運用」の強制の記述と「不可逆な操作は都度確認」節を改める |
| `docs/harness/10-workflow.md` | S8 の注意書きと「よくある逸脱」表の「承認を取ってから」を改める |
| `.claude/hooks/protect-commands.ps1` | git の書き込み操作の `ask` を `gh pr create` だけに絞る |
| `.claude/hooks/guard-source-edit.ps1` | deny 文の「ブランチ切替は承認を取ること」を消す |
| `.claude/hooks/session-start-context.ps1` | 毎セッションの注意書きを新しい規範に合わせる |
| `.claude/settings.json` | D3 の `allow` を足す |

## 7. 実装手順

### グループ A — 規範
- [x] **A1** `CLAUDE.md` を改める
- [x] **A2** `00-ways-of-working.md` を改める
- [x] **A3** `10-workflow.md` を改める

### グループ B — 強制
- [x] **B1** `protect-commands.ps1` を改める
- [x] **B2** `guard-source-edit.ps1` を改める
- [x] **B3** `session-start-context.ps1` を改める
- [x] **B4** `settings.json` を改める
- [x] **B5** `protect-commands.ps1` に入力を流し、commit / push / switch が素通し・`gh pr create` と force push が `ask` になることを確かめる

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功

## 8. テスト一覧

追加なし。

> **テストで守れない範囲:** フックの判定は xUnit の対象外。B5 で手で入力を流して確かめる。

## 9. 未解決の質問

1. **merge / rebase も確認なしにしてよいか** — D1 で「PR のみ」の文言どおり外した。*既定案: 外す。確認をお願いします。*

## 10. 前提

- PreToolUse フックの `ask` は `settings.json` の `allow` より優先される（force push が止まり続ける根拠）。

---

## 実行結果 (2026-11-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 507 件成功 / 0 件失敗 / 0 件スキップ
- B5（`protect-commands.ps1` の判定）: `git add` / `commit` / `push -u` / `switch -c` / `merge` は素通し、
  `gh pr create` / `git push --force` / `git reset --hard` は `ask`
- 計画からの逸脱: なし
