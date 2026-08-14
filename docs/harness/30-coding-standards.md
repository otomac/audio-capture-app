# 30 — コーディング標準（C# 12 / .NET 8）

**ベースライン:** C# 12 / .NET 8（`net8.0-windows`）、WPF。

多くは機械強制される（[.editorconfig](../../.editorconfig) と
[Directory.Build.props](../../Directory.Build.props)）。強制できないものは
**「規約（機械強制なし）」** と明示する。そこはレビューで担保する。

規約とツール設定を **絶対に乖離させない**。片方だけ直すのは禁止。

---

## 1. ルールがどこにあるか

| ファイル | 何を決めているか |
|---|---|
| [Directory.Build.props](../../Directory.Build.props) | 全プロジェクト共通のコンパイラ／解析設定（nullable・`AnalysisMode`・警告のエラー化） |
| [Directory.Packages.props](../../Directory.Packages.props) | NuGet バージョンの一元管理（CPM） |
| [.editorconfig](../../.editorconfig) | 書式・言語スタイル・命名・**ルール別 severity** |
| 本書 | *なぜ* そうなっているか＋機械強制できない規約 |

配置の根拠は [70-build-configuration.md](./70-build-configuration.md)。

## 2. severity モデル

`TreatWarningsAsErrors=true` が効いているため、**各ルールに付いた severity がそのまま意味を持つ**。

- **`warning`** → **ビルドが落ちる**。正しさと一貫性のためのルールに使う。
- **`suggestion` / `silent`** → IDE のヒントのみ。ビルドは落ちない。純粋な好みに使う。

個別ルールの調整と、現在許可されている緩和の一覧は [40-quality-gates.md](./40-quality-gates.md) にある。
`.editorconfig` を直接編集して severity を下げる場合、**必ず理由コメントを 1 行添える**。

---

## 3. 言語・スタイル（`.editorconfig` で強制）

### 名前空間

- **ファイルスコープ名前空間**（`namespace AudioCaptureApp.Models;`）。1 ファイル 1 名前空間。
- 名前空間は **フォルダ構造と一致** させる（`dotnet_style_namespace_match_folder`）。

### `var`

本プロジェクトは **明示的な型を既定** とする（`csharp_style_var_* = false`）。
ただし既存コードには `var` が相当数あるため、この設定は `suggestion` に留めている
（一括書き換えは差分を無意味に膨らませるため）。

**規約：** 新規コードは明示的な型で書く。右辺から型が自明（`new T()` / キャスト）な場合の `var` は許容する。

### 波かっこ

`if` / `for` / `while` 等の本体には波かっこを付ける（`csharp_prefer_braces = true`）。
Allman スタイル（`csharp_new_line_before_open_brace = all`）。

### メンバー

- `int` / `string` を使う。`Int32` / `String` は使わない。
- `this.` 修飾は付けない。
- 非インターフェースメンバーにはアクセシビリティ修飾子を明示する。
- `readonly` フィールド、自動プロパティ、`??` / `?.` / パターンマッチ / switch 式などの現代的な書き方を優先する。
- 使わない代入は破棄（`_ = ...`）で明示する。

### 書式

4 スペースインデント、**CRLF**、UTF-8、末尾空白なし。すべて `dotnet format` が自動修正する。

---

## 4. 命名（`.editorconfig` で強制）

| 対象 | 規則 | 例 |
|---|---|---|
| クラス / 構造体 / 列挙型 / デリゲート | PascalCase | `AudioCaptureService` |
| インターフェース | `I` + PascalCase | `IValueConverter` |
| メソッド / プロパティ / イベント | PascalCase | `StartRecording` |
| 定数 | PascalCase | `MaxRetries` |
| private / internal フィールド | `_camelCase` | `_micBuffer` |
| パラメーター / ローカル変数 | camelCase | `deviceId` |
| 型パラメーター | `T` + PascalCase | `TResult` |

### 非同期メソッドの `Async` サフィックス

`Task` / `ValueTask` を返すメソッドは **`Async` で終える**（`StopRecordingAsync` 等）。

> **強制の穴（レビューで担保）。** EditorConfig の命名ルールは `async` **修飾子** にしか反応せず、
> `async` を付けずに `Task` を直接返すメソッドは検出できない。さらに IDE 命名アナライザー
> （`IDE1006`）はコマンドラインビルドで報告されないことがある。**命名は手で正しく付けること。**
> 詳細は [40-quality-gates.md §4](./40-quality-gates.md#4-既知の穴)。

### テストメソッド名

`対象_条件_期待結果` 形式で、アンダースコア区切りを使う（`JsonRoundTrip_PreservesValues` 等）。
このためテストプロジェクトでは CA1707（識別子にアンダースコアを含めない）を無効化している。

---

## 5. 型と設計の規約（機械強制なし）

### Model は POCO に留める

`Models/` の型はデータの入れ物であり、ロジックを持たない。
不変にできるものは `init` アクセサーと `required` で不変にする（`AudioDevice` が既存の例）。

### DI はコンストラクター注入のみ

DI コンテナは使わないが、依存は **コンストラクター引数か明示的な setter メソッド** で渡す。
サービスロケーターや静的な可変グローバル状態は禁止。

### プライマリコンストラクターは使わない

**規約（機械強制なし）。** プライマリコンストラクターはパラメーターを隠しフィールドとして捕捉するため、
`readonly` フィールドへの代入や不変条件の検証と相性が悪い。明示的なコンストラクターを書く。

### record の使いどころ

`record` は **値オブジェクトと解析用 DTO** に限る。状態を持ち替えるサービスやビューモデルには使わない。

---

## 6. 非同期の規律（規約 — レビューで担保）

[20-architecture-standards.md §3](./20-architecture-standards.md#3-スレッドモデルの規範) の再掲。
本アプリは並行スレッドが多く、ここが最も壊れやすい。

- **`.Result` / `.Wait()` を使わない。** 同期待ちは UI スレッドでデッドロックする。
- **`async void` はイベントハンドラーのみ。**
- 時間のかかる処理は `Task.Run` で UI スレッドから退避する。
- UI スレッド以外からのバインドプロパティ更新は
  `Application.Current.Dispatcher.BeginInvoke` を必ず経由する。
- `ConfigureAwait(false)` は **付けない**。WPF アプリでは継続を UI コンテキストに戻す必要があるため、
  アナライザー CA2007 を無効化している（[40-quality-gates.md](./40-quality-gates.md) 参照）。

## 7. リソース破棄

オーディオ・ファイル・Whisper のハンドルを大量に扱うため、破棄漏れは実害に直結する。

- `IDisposable` なフィールドを持つ型は自身も `IDisposable` を実装する（CA1001）。
- `Dispose(bool)` パターンを正しく実装する（CA1063）。`Dispose()` の中で
  `GC.SuppressFinalize(this)` を呼ぶ（CA1816）。
- ローカルで作った `IDisposable` は `using` で確実に破棄する（CA2000）。

これらは現在アナライザー上の技術的負債として一時緩和されている。
新規コードでは最初から守ること（[40-quality-gates.md](./40-quality-gates.md) の負債一覧参照）。

## 8. カルチャー依存

`ToString` / `Parse` / 比較にはカルチャーを明示する（CA1305 / CA1307）。

- 画面表示・ユーザー向け文字列 → `CultureInfo.CurrentCulture`
- ファイル名・設定値・ログなど機械可読な文字列 → `CultureInfo.InvariantCulture`
- 文字列比較 → `StringComparison` を明示（既定は `StringComparison.Ordinal`）

## 9. 標準を変えたいとき

これらのファイル **そのものが標準** である。回避策を書くのではなく、ファイルを直す。

- アナライザーの誤検知への対処は [40-quality-gates.md §3](./40-quality-gates.md#3-抑止の書き方) の書式に従う。
- 「規約（機械強制なし）」に該当する内容を変えるときは、**同じ変更の中で本書も更新** する。
  文書と実態を乖離させない。
