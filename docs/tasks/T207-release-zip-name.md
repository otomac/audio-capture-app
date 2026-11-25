# T207 — リリース zip のファイル名を統一する

> **状態:** 完了 — 2026-11-25
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

リリースの zip の名前にタグ名がそのまま入り、`AudioCaptureApp-release_2026.10.18_beta-win-x64.zip` のように `release_` が付いていた。
zip に同梱している `VERSION`（[T189](./T189-version-file-in-release-zip.md)）と同じ値で名付け、名前と中身のバージョンを揃える。

## 2. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | リリースの名前 | **`AudioCaptureApp-<VERSION>-win-x64.zip`**（台帳の既定案）。例 `AudioCaptureApp-2026.09.23-win-x64.zip`。`VERSION` がタグと一致することは、ビルドより前のステップで CI が確かめている（T189）ので、タグ名から `release_` を削った値と同じになる |
| D2 | リリース以外の CI ビルド | 今までどおり `AudioCaptureApp-ci-<実行番号>-win-x64.zip`。`develop` の `VERSION` は直前のリリースの値なので、それで名付けるとリリースと見分けがつかない |
| D3 | 判定の条件 | `refs/tags/*` から `refs/tags/release_*` に狭める。この workflow がタグで動くのは `release_*` だけなので、今までと同じ範囲 |

## 3. 仕様書への影響

- [x] `docs/spec/02_architecture.md` の「配布」の行に zip の名前を書いた

## 4. アーキテクチャへの影響

- ADR: 不要（CI の名付けだけ）

## 5. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `.github/workflows/build-desktop.yml` | 「Create zip package」で、リリースは `VERSION` の値で名付ける |
| `docs/spec/02_architecture.md` | 上の 3 |

## 6. 確認

workflow の名付けの部分をそのまま取り出し、環境変数を変えてローカルで動かした（`VERSION` は `2026.09.28`）:

| `GITHUB_REF` | zip の名前 |
|---|---|
| `refs/tags/release_2026.09.28` | `AudioCaptureApp-2026.09.28-win-x64.zip` |
| `refs/heads/develop` | `AudioCaptureApp-ci-123-win-x64.zip` |
| `refs/pull/79/merge` | `AudioCaptureApp-ci-123-win-x64.zip` |

> **残り:** リリースタグでの CI はまだ走らせていない（次のリリースで確かめる）。GitHub Release に上げるファイル名は
> `steps.package.outputs.zip_name` を通るので、ここが変われば一緒に変わる。

---

## 実行結果 (2026-11-25)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 567 件成功 / 0 件失敗 / 0 件スキップ（追加なし。CI の設定だけの変更）
- 計画からの逸脱: なし
