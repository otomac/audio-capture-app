# T189 — リリース zip に `VERSION` と `README.md` を同梱する

> **状態:** 進行中 — 2026-09-28
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

リリース zip を展開しただけでは、それがどのリリースかも、何をするアプリかも分からない。
リリースバージョンを書いた `VERSION` をリポジトリに用意し、`README.md` と一緒にリリース zip へ同梱する。

## 2. スコープ境界

**やること**
- リポジトリ直下に `VERSION` を置き、リリースバージョンを 1 行で書く
- `build-desktop` の zip 作成で、発行物に加えて `VERSION` と `README.md` を zip の直下に入れる
- `release_*` タグのビルドでは、`VERSION` がタグと一致することを確かめ、食い違えば CI を失敗させる
- 仕様書の「配布」行と、ハーネスのリポジトリ配置図に `VERSION` を書く

**やらないこと（重要）**
- **アプリ本体（`.csproj` のアセンブリバージョン・画面表示）へのバージョンの反映。** 依頼は配布物への同梱のみ
- **zip のファイル名の決め方の変更。** 今までどおりタグ名（タグでなければ `ci-<実行番号>`）から作る
- **`README.md` の書き換え。** 中身はそのまま入れる。README が参照する画像（`manual/image/main-screen.png`）は同梱しないので、zip 内の README では画像が表示されない
- **`LICENSE` など、依頼に無いファイルの同梱**
- **`VERSION` の自動更新（タグ付けや日付からの書き換え）。** 手で書き換え、タグとの一致を CI で確かめる

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | `VERSION` の置き場と書式 | **リポジトリ直下の `VERSION`。1 行でバージョンだけを書く。** 形式は既存のリリースタグ `release_YYYY.MM.DD[.N]` から `release_` を除いたもの（例 `2026.09.23`）。`release_` はワークフローを起動するためのタグの接頭辞で、バージョンの一部ではない |
| D2 | 初期値 | **`2026.09.28`。** 次のリリースを `release_2026.09.28` で出す想定（利用者の選択 2026-09-28。最新のリリース `2026.09.23` との二択）。以後はリリースの前に、付けるタグに合わせて書き換える |
| D3 | タグとの整合 | **`release_*` タグのビルドでは、`VERSION`（前後の空白・改行を除く）がタグ名から `release_` を除いたものと一致しなければ失敗させる。** 手で書き換えるファイルなので更新忘れは必ず起きる。忘れたまま出すと zip の `VERSION` が実際のリリースと食い違い、同梱する意味が無くなる。チェックアウト直後に置き、ビルド・テストより前に落とす。タグ以外（push / PR / 手動実行）では確かめない |
| D4 | 同梱の仕方 | **`Compress-Archive -Path "./publish/*", "./VERSION", "./README.md"`。** 3 つとも zip の直下に入る（`VERSION` と `README.md` は `AudioCaptureApp.exe` と同じ階層）。`publish/` へはコピーしない（発行物のフォルダを変えずに済む）。発行物に同名のファイルが無いことは CI 実行 #72 の発行物一覧で確認した |

## 4. 仕様書への影響

- [-] `docs/spec/01_requirements.md` — アプリの機能は変わらない
- [x] `docs/spec/02_architecture.md` — §1 技術スタック表の「配布」行に、zip の直下に `VERSION` と `README.md` を同梱すること、`VERSION` の書式とタグとの一致を CI で確かめることを追記
- [-] `docs/spec/03_class_diagram.md` — クラス構成は変わらない
- [-] `docs/spec/04_sequence_diagram.md` — 処理順序は変わらない

## 5. アーキテクチャへの影響

層構成・依存方向・スレッドモデルに触れない。CI の配布物の構成だけを変える。

- ADR: **不要**（配布物に説明用のファイルを 2 つ足すだけで、設計判断の変更を伴わない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `VERSION` | 新規。`2026.09.28` |
| `.github/workflows/build-desktop.yml` | タグとの一致を確かめる手順を追加（D3）、zip 作成に `VERSION` と `README.md` を追加（D4） |
| `docs/spec/02_architecture.md` | §4 のとおり |
| `docs/harness/70-build-configuration.md` | §1 のリポジトリ配置図に `VERSION` を追加 |
| `docs/tasks/backlog.md` | T189 を起票・完了 |

## 7. 実装手順

### グループ A — `VERSION` と同梱
- [x] **A1** `VERSION` を作る（`VERSION`）
- [x] **A2** `release_*` タグのビルドで `VERSION` とタグの一致を確かめる手順を足す（`.github/workflows/build-desktop.yml`）
- [x] **A3** zip 作成に `VERSION` と `README.md` を足す（`.github/workflows/build-desktop.yml`）
- [x] **A4** 仕様書とリポジトリ配置図を直す（`docs/spec/02_architecture.md` / `docs/harness/70-build-configuration.md`）

### グループ B — 手順の動作確認（PowerShell 7 で CI の手順を再現）
- [x] **B1** 発行物を模したフォルダで zip 作成の手順を実行し、zip の直下に `VERSION` と `README.md` があり、発行物のサブフォルダの構成が変わらないことを確かめる
- [x] **B2** タグとの一致を確かめる手順を、一致・不一致・CRLF 改行の `VERSION` で実行し、不一致のときだけ失敗することを確かめる

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す
- [ ] **Z5** ブランチ上の CI（windows-latest）で zip 作成まで成功すること
- [ ] **Z6** 次のリリース（`release_*` タグ）で、zip に `VERSION` と `README.md` が入っていることを確かめる（利用者）

## 8. テスト一覧

追加なし。CI の手順の変更で、ユニットテストの対象になるコードは変えない。

> **テストで守れない範囲:** 実際の `release_*` タグでの動き（タグとの一致確認と GitHub Release への添付）。
> タグを付けないと走らないため、手順は PowerShell 7 で同じスクリプトを再現して確かめる（グループ B）。

## 9. 未解決の質問

なし。

## 10. 前提

- リリースのタグは今後も `release_<バージョン>` の形で付ける（`build-desktop.yml` の `tags: release_*`）。
- 発行物（`dotnet publish` の出力）に `VERSION` / `README.md` という名前のファイルは現れない。現れた場合は zip 作成時に名前が衝突する。

---

## 実行結果 (2026-09-28、途中経過)

作業環境（Linux）から `builds.dotnet.microsoft.com` への接続がネットワークポリシーで拒否され、.NET SDK を
取得できない。G1〜G3 はブランチ上の CI（windows-latest）で実測して追記する。変更は CI 定義と文書と
`VERSION` だけで、`.cs` / `.xaml` / `.csproj` / `Directory.*.props` には触れていない。

- **B1**（PowerShell 7.5.3、ワークフローの `run:` をそのまま取り出し、Actions の pwsh と同じ前後処理を付けて実行）:
  発行物を模したフォルダ（直下のファイル 3 つ、`ja/`、`runtimes/<…>/` の入れ子）で zip を作成。
  旧コマンド（`./publish/*` のみ）の zip と比べて **増えたのは `README.md` と `VERSION` の 2 つだけ・減ったもの 0**。
  どちらも zip の直下にあり、リポジトリのファイルとバイト単位で同一。`zip_name` の出力も従来どおり
- **B2**（同上）: `GITHUB_REF_NAME` と `VERSION` の組み合わせ 8 通り
  - 成功（exit 0）: LF / CRLF / UTF-8 BOM + CRLF / 末尾改行なし の `2026.09.23` 対 `release_2026.09.23`、`2026.03.22.2` 対 `release_2026.03.22.2`
  - 失敗（exit 1、`::error file=VERSION::` を出力）: `2026.09.23` 対 `release_2026.10.01`、`2026.03.22` 対 `release_2026.03.22.2`、`VERSION` が無い
- CI 実行 #72（`develop` の d11db1e、windows-latest）の発行物一覧に `README.md` / `VERSION` は無い（名前の衝突なし）
- `build-desktop.yml` は YAML として読める
