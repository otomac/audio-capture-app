# T102 — カルチャー・文字列比較の明示

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

カルチャー依存の `ToString` と `StringComparison` 未指定の文字列比較（CA1305 / CA1307）を明示化し、
`.editorconfig` の負債行を削除する。

## 2. スコープ境界

**やること**
- CA1305 の 2 箇所に `IFormatProvider` を明示する
- CA1307 の 2 箇所（テスト）に `StringComparison` を明示する
- `.editorconfig` から T102 の負債 2 行を削除する

**やらないこと（重要）**
- **補間文字列（`$"..."`）の一括 `FormattableString` 化** — CA1305 は補間を報告しておらず、
  対象外。UI 表示文字列の見た目を変えない
- **既存の `StringComparison.OrdinalIgnoreCase` 指定箇所**（`IsSupportedAudioExtension`）は既に正しい
- **T103 / T104 / T105 の対象ルール**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 録音ファイル名 `now.ToString("yyyyMMdd_HHmmss")`（`AudioCaptureService`） | **`CultureInfo.InvariantCulture`**。ファイル名は機械可読な文字列であり、和暦等のカルチャーで桁が変わってはならない（[30-coding-standards.md §8](../harness/30-coding-standards.md#8-カルチャー依存)） |
| D2 | 経過時間 `elapsed.ToString(@"hh\:mm\:ss")`（`MainViewModel`） | **`CultureInfo.CurrentCulture`**。画面表示のユーザー向け文字列であるため（同 §8） |
| D3 | テストの `Assert.Contains(部分文字列, 対象)` | **`StringComparison.Ordinal`**。パス断片の完全一致判定であり、言語依存の照合は不要 |

## 4. 仕様書への影響

**仕様非影響。** 理由: 公開シグネチャも外から見た振る舞いも変わらない静的解析の是正である
（[50-spec-standards.md §2](../harness/50-spec-standards.md#2-章立てと更新対象の判定)「更新が不要な変更」）。
ファイル名書式 `yyyyMMdd_HHmmss` 自体は変わらず、カルチャー非依存であることを明示しただけである。

## 5. アーキテクチャへの影響

- ADR: **不要**。層・依存方向・スレッドモデルのいずれにも触れない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AudioCaptureService.cs` | `using System.Globalization;` 追加、ファイル名生成を `InvariantCulture` に |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `using System.Globalization;` 追加、経過時間表示を `CurrentCulture` に |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | `Assert.Contains` に `StringComparison.Ordinal` を指定（2 箇所） |
| `.editorconfig` | T102 の負債 2 行（CA1305 / CA1307）を削除 |

## 7. 実装手順

### グループ A — 実装
- [x] **A1** `AudioCaptureService.StartRecording` のファイル名生成に `InvariantCulture`
- [x] **A2** `MainViewModel` の `_clockTimer.Tick` に `CurrentCulture`
- [x] **A3** `AppSettingsTests.DefaultValues_AreCorrect` の 2 箇所に `StringComparison.Ordinal`

### グループ B — 緩和の削除
- [x] **B1** `.editorconfig` の T102 負債 2 行を削除
- [x] **B2** [40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和) の負債表から T102 行を削除

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功

## 8. テスト一覧

**追加なし。** ファイル名生成（`StartRecording`）は実オーディオデバイスを要求するため
ユニットテストの対象外。既存の `AppSettingsTests` を修正して回帰を担保する。

> **テストで守れない範囲:** 和暦等の非グレゴリオ暦カルチャー下でのファイル名生成。
> `InvariantCulture` 指定という実装事実で担保する。

## 9. 未解決の質問

なし。

## 10. 前提

- CA1305 / CA1307 の指摘箇所は、緩和を外したビルドで実測した 4 件がすべてである。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: なし
