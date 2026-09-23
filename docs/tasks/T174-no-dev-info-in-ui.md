# T174 — アプリの画面に開発情報（要件 ID・タスク ID）が出ている

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

利用者が読む文字列から開発情報を取り除き、**再発しないよう規範と品質ゲートで止める**。
要件 ID やタスク ID はリポジトリを読める開発者にしか意味がなく、利用者には「調べようのない記号」になる。

## 2. 混入していた箇所（`grep` で全画面文字列を走査。この 2 件のみ）

| 箇所 | 修正前 | 修正後 |
|---|---|---|
| `WhisperModelsWindow.xaml`（利用者の報告） | 登録時にはファイルの存在だけを確認します。読み込めるかは選択したときに分かります（**REQ-MODELWIN-05**）。 | 登録時にはファイルがあることだけを確認します。モデルとして読み込めるかどうかは、実際に選んだときに分かります。 |
| `FileTranscriptionOptionsWindow.xaml` | 人数を指定すると結果が悪くなることがあります（**実測 T147**）。 | 人数を指定すると、かえって精度が落ちることがあります（実際の音声で確認しています）。 |

C# 側（`StatusMessage`・ダイアログ・`Error` イベント）には混入なし。

## 3. スコープ境界

**やること**
- 上記 2 件の文言修正
- 遵守事項を [30-coding-standards.md §9](../harness/30-coding-standards.md) に追加
- 仕様側に **NFR-10** を新設
- 画面文字列を走査するテスト（`UiTextTests`）で品質ゲート G3 から機械的に止める

**やらないこと（重要）**
- **`settings.json` とそのキー名の記述は消さない** — 利用者が手編集する対象であり、開発情報ではない（REQ-CFG-06 / 10 / 11）
- **コード内のコメント・XAML のコメントからは消さない** — 開発者が読むものであり、要件 ID との対応は追跡に要る
- **ドキュメント（README・docs/）は対象外**

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 禁止する対象 | 要件 ID（`REQ-*` / `NFR-*`）・タスク ID（`T123`）・ADR 番号・クラス名／メソッド名／プロパティ名・内部の設計用語 |
| D2 | 例外 | 利用者自身が操作・編集する対象の名前（`settings.json` とキー名、モデルファイル名、拡張子、デバイス名、OS の機能名） |
| D3 | 根拠の書き方 | 出典の記号ではなく**事実そのもの**を書く（「実測 T147」→「実際の音声で確認しています」） |
| D4 | 強制方法 | レビュー任せにせず `UiTextTests` で走査する。XAML はビルド後のアセンブリから戻せないため、`[CallerFilePath]` でソースツリーの位置を得て `.xaml` を直接読む |
| D5 | 走査範囲 | `.xaml` の `Text` / `Content` / `ToolTip`（`{Binding …}` は除く）と、ViewModel / Service の利用者向け文言を返す `internal static` メソッド・定数 |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — **NFR-10 新設**
- 変更なし: `02` / `03` / `04`（振る舞いは変わらない）

## 6. 規範への影響

- [x] `docs/harness/30-coding-standards.md` — **§9「利用者に見せる文字列（`UiTextTests` で強制）」を新設**（既存の §9 は §10 へ繰り下げ）

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/WhisperModelsWindow.xaml` | 注記から `REQ-MODELWIN-05` を除去 |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | 注記から `実測 T147` を除去 |
| `AudioCaptureApp.Tests/UiTextTests.cs` | 新規（走査テスト） |

## 8. テスト一覧

- **`XamlDisplayText_HasNoDeveloperIdentifiers`** — すべての `.xaml`（7 ファイル）の表示属性を走査
- **`XamlFiles_AreFound`** — ソースツリーを辿れず 0 件で「緑」になる事故を防ぐ
- **`ViewModelText_HasNoDeveloperIdentifiers`** — ViewModel / Service の利用者向け文言 37 件

**テストが実際に検出することを確認した**（2026-09-22）: `WhisperModelsWindow.xaml` の注記へ
`（REQ-MODELWIN-05）` を一時的に戻すと `XamlDisplayText_HasNoDeveloperIdentifiers` が
「利用者に見せる文字列に要件 ID『REQ-MODELWIN-05』が含まれています（NFR-10）」で失敗し、
1 件失敗 / 369 件成功になった。確認後はファイルを元に戻した。

## 9. 未解決の質問

なし。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 370 件成功 / 0 件失敗 / 0 件スキップ（320 → 370、+50）
- 計画からの逸脱: なし
