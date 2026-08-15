# T106 — `.claude/settings.local.json` を git 管理から外す

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

`.claude/settings.local.json` は開発者ごとの個人設定（ツール許可リスト）であり、
リポジトリで共有すべきものではない。git 管理から外し、`.gitignore` に登録する。

## 2. スコープ境界

**やること**
- `.gitignore` に `.claude/settings.local.json` を追加する
- `git rm --cached` で追跡対象から外す（**作業ツリー上のファイルは残す**）

**やらないこと（重要）**
- **`.claude/settings.json` の扱い変更** — こちらはプロジェクト共有の設定であり、追跡し続けるのが正しい
- **`.claude/hooks/` の扱い変更** — ハーネスの強制点そのものであり、共有が必要
- **git 履歴の書き換え** — 決定 D2 参照
- **`.gitignore` のその他の整理**（`bin/` / `obj/` 等）— 本タスクの目的外。必要なら別途起票する
- **コミット／プッシュ** — 不可逆な操作は都度依頼を受けてから行う
  （[00-ways-of-working.md](../harness/00-ways-of-working.md#不可逆な操作は都度確認)）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 無視パターンの書き方 | **`.claude/settings.local.json` をフルパスで指定する。** `.claude/*` のような広いパターンは `settings.json` と `hooks/` まで巻き込むため使わない |
| D2 | 過去の履歴に残った内容をどうするか | **履歴は書き換えない。** 追跡されていた内容はツール許可リスト（`Bash(dotnet test *)` 等）5 行のみで、資格情報・トークン・個人情報を含まない。`filter-repo` 等による履歴改変は共有履歴の破壊コストに見合わない |
| D3 | 作業ツリーのファイルをどうするか | **残す。** `git rm --cached`（`--cached` 付き）を使い、ローカルの許可設定はそのまま機能させる |

## 4. 仕様書への影響

**仕様非影響。** アプリケーションの仕様・振る舞い・構造のいずれにも関係しない、
リポジトリ運用（開発ハーネス）の変更である
（[50-spec-standards.md §2](../harness/50-spec-standards.md#2-章立てと更新対象の判定)「更新が不要な変更」）。

## 5. アーキテクチャへの影響

- ADR: **不要**。ソースコードを 1 行も変更しない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `.gitignore` | `.claude/settings.local.json` を追加 |
| （git インデックス） | `.claude/settings.local.json` の追跡を解除（ファイルは残す） |

## 7. 実装手順

- [x] **A1** `.gitignore` に無視パターンを追加
- [x] **A2** `git rm --cached .claude/settings.local.json`
- [x] **A3** ファイルが作業ツリーに残っていること、`git status` で無視されていることを確認

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功

> ソース非変更のためゲートは回帰確認としてのみ実行する。

## 8. テスト一覧

**追加なし。** git の追跡状態はユニットテストの対象ではない。A3 の実測で担保する。

## 9. 未解決の質問

なし。

## 10. 前提

- `.claude/settings.local.json` の内容に秘匿情報が含まれない（D2 の根拠。着手時に中身を確認済み）。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件（`--no-incremental` で全再ビルド）
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- A3 の実測:
  - `.claude/settings.local.json` は作業ツリーに存在（201 バイト）
  - `git check-ignore -v` → `.gitignore:6:.claude/settings.local.json` で無視されている
  - `git ls-files .claude/` の残りは `hooks/*.ps1` 3 件と `settings.json` のみ
  - インデックス上は削除がステージされた状態（`D  .claude/settings.local.json`）。
    **コミットは未実施**（依頼待ち）
- 計画からの逸脱: なし
