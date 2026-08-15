# T105 — 軽微な静的解析指摘の解消

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

`static` にできるメンバー（CA1822）と長さ 0 の配列確保（CA1825）を解消し、
`.editorconfig` の負債行（＝技術的負債セクションの最後の 2 行）を削除する。

## 2. スコープ境界

**やること**
- CA1822 の 3 箇所（`MainWindow.TryGetSingleDroppedFile` / `SettingsService.Load` / `SettingsService.Save`）
- CA1825 の 1 箇所（`AudioCaptureServiceTests`）
- `.editorconfig` から T105 の負債 2 行を削除し、**技術的負債セクションごと削除**する
- [40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和) を
  「負債は解消済み」の記述に書き換える

**やらないこと（重要）**
- **`SettingsService` を `static class` にすること／`MainViewModel` から `_settingsService` を消すこと**
  — 決定 D1 参照。3 つの Service を ViewModel が直接 `new` する構造
  （[20-architecture-standards.md §2](../harness/20-architecture-standards.md#2-意図的にやっていないこと変えるなら-adr)）を崩さない
- **設定ファイルの保存場所・書式の変更**
- **`InverseBoolConverter` / `LevelMeterControl` など、CA1822 が報告していない箇所の `static` 化**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | `SettingsService.Load` / `Save`（CA1822）の直し方 | **メソッドを `static` にするのではなく、`static readonly` だった設定パス／`JsonSerializerOptions` を `readonly` インスタンスフィールドへ移す。** メソッドが「インスタンスデータを使わない」という指摘の根本原因は、型の状態（設定ファイルの場所）が `static` に置かれていたことにある。インスタンス状態に戻せば `Load` / `Save` は自然にインスタンスメソッドとして正当になり、`MainViewModel` の呼び出し側・仕様書のクラス図・他 2 つの Service との一貫性（ViewModel が直接 `new` する）をいずれも変えずに済む |
| D2 | `MainWindow.TryGetSingleDroppedFile`（CA1822） | **`private static` にする。** `DragEventArgs` だけを見るヘルパーであり、インスタンス状態と無関係。`private` なので外部への影響はない |
| D3 | `_settingsFilePath` の初期化方法 | **明示的なコンストラクターで組み立てる。** フィールド初期化子は他のインスタンスフィールドを参照できないため。プライマリコンストラクターは規約で不使用（[30-coding-standards.md §5](../harness/30-coding-standards.md#5-型と設計の規約機械強制なし)） |
| D4 | CA1825（`new byte[0]`） | **`byte[] buffer = [];`**（コレクション式）。同ファイルの既存記法（`float[] expected = [...]`）に合わせる |

## 4. 仕様書への影響

**仕様非影響。** 理由:
- D1 により `SettingsService` の公開シグネチャ（`Load()` / `Save(AppSettings)`）も
  `MainViewModel` との関係も変わらない。クラス図の `-string SettingsFilePath` および
  `MainViewModel "1" --> "1" SettingsService` はそのまま正しい
- D2 は `private` メンバーの修飾子変更であり、公開メソッドの増減にあたらない
- D4 はテストコード

（[50-spec-standards.md §2](../harness/50-spec-standards.md#2-章立てと更新対象の判定)「更新が不要な変更」）

## 5. アーキテクチャへの影響

- ADR: **不要**。D1 でまさに構造を変えない道を選んでいる。層・依存方向・スレッドモデルのいずれにも触れない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/SettingsService.cs` | `static readonly` 3 つを `readonly` インスタンスフィールドへ、コンストラクター追加 |
| `AudioCaptureApp/MainWindow.xaml.cs` | `TryGetSingleDroppedFile` を `private static` に |
| `AudioCaptureApp.Tests/AudioCaptureServiceTests.cs` | `new byte[0]` → `[]` |
| `.editorconfig` | 技術的負債セクションを削除（T105 の 2 行が最後の残り） |
| `docs/harness/40-quality-gates.md` | §5 を「負債は解消済み」に書き換え |
| `docs/harness/30-coding-standards.md` | §7 末尾の「現在一時緩和されている」という記述が事実と食い違うため修正 |

## 7. 実装手順

### グループ A — 実装
- [x] **A1** `SettingsService` の状態をインスタンス化（D1 / D3）
- [x] **A2** `MainWindow.TryGetSingleDroppedFile` を `static` に（D2）
- [x] **A3** `AudioCaptureServiceTests` の `new byte[0]` を `[]` に（D4）

### グループ B — 緩和の削除
- [x] **B1** `.editorconfig` の技術的負債セクションを削除
- [x] **B2** `40-quality-gates.md` §5 を書き換え
- [x] **B3** `30-coding-standards.md` §7 の陳腐化した記述を修正

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功

## 8. テスト一覧

**追加なし。** `SettingsService` は `%APPDATA%` への実ファイル I/O を伴い、
ユニットテストの対象外（[20-architecture-standards.md §7](../harness/20-architecture-standards.md#7-テスト容易性の設計規範)）。
既存の `AudioCaptureServiceTests.BytesToFloats_EmptyBuffer_ReturnsEmptyArray` が
D4 の変更後も同じことを保証する。

> **テストで守れない範囲:** `SettingsService` のパス組み立てがコンストラクター移行後も
> 同一であること。`Path.Combine` の引数を変えていないという差分の事実で担保する。

## 9. 未解決の質問

なし。

## 10. 前提

- CA1822 / CA1825 の指摘箇所は、緩和を外したビルドで実測した 4 件がすべてである。
- `SettingsService` のインスタンスはアプリ全体で 1 つ（`MainViewModel` が生成）であり、
  `JsonSerializerOptions` をインスタンスフィールドに移しても生成回数は増えない。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件（`--no-incremental` で全再ビルド）
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: `30-coding-standards.md` §7 の修正（B3）を追加した。
  §7 が「これらは現在技術的負債として一時緩和されている」と書いており、負債セクション削除後は
  事実と食い違うため（[30-coding-standards.md §9](../harness/30-coding-standards.md#9-標準を変えたいとき)
  「文書と実態を乖離させない」）。
