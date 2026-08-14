# ADR-0001 — ベースラインアーキテクチャ（3 層 / DI コンテナなし / 抽象化なし）

> **状態:** 承認済み
> **日付:** 2026-08-14
> **関連タスク:** [T100](../tasks/T100-dev-harness.md)

## 背景

本 ADR は新しい決定ではなく、**すでに実装に埋め込まれている決定を明文化する** ものである。

AudioCaptureApp は 2026-03 から実装が進み、Models / ViewModels / Services の 3 層構成、
DI コンテナ不使用、Service のインターフェース抽象なし、という形で動いている。
これらは `CLAUDE.md` に「シンプル優先」として一行触れられているだけで、
**なぜそうなのか・いつ見直すのか** はどこにも書かれていなかった。

書かれていない決定は、善意で壊される。ハーネス整備（T100）にあたり、既定路線を明示的な決定として固定する。

## 現状

- 3 層構成（実体は [docs/spec/02_architecture.md §2](../spec/02_architecture.md)）
- `MainWindow` が `MainViewModel` を直接 `new` して `DataContext` に設定する
- `MainViewModel` が `AudioCaptureService` / `TranscriptionService` / `SettingsService` を直接生成する
- NAudio・Whisper.net を独自ラップせず直接使う
- テスト容易性は `InternalsVisibleTo` ＋ `internal static` の純粋関数で確保している

## 選択肢

### 案 A — 現状構成を正式な決定として固定する（採用）

- **内容:** 3 層・DI なし・抽象化なしを規範として明文化し、逸脱には ADR を要求する。
- **利点:** 追加の実装コストゼロ。間接性が無いので読めば分かる。既存コードと規範が一致する。
- **欠点:** 規模が大きくなったときに窮屈になる。テストは純粋関数の切り出しに依存する。
- **影響範囲:** 文書のみ。

### 案 B — DI コンテナ ＋ Service インターフェースを導入する

- **内容:** `Microsoft.Extensions.DependencyInjection` を入れ、Service をインターフェース化してモック可能にする。
- **利点:** ViewModel のユニットテストが書きやすくなる。差し替えが容易。
- **欠点:** 実装が 1 つしかない抽象を大量に作ることになる。単一ウィンドウ・単一 ViewModel の
  規模に対して間接性のコストが見合わない。新規パッケージの追加を伴う。
- **影響範囲:** 全 Service、`App.xaml.cs`、`MainWindow`、テスト。

### 案 C — 何もしない（決定を明文化しない）

現状のまま。判断基準が無いため、担当者ごとに DI やインターフェースが部分的に導入され、
構成が混在する。**最も悪い結果になる** ため却下。

## 決定

**案 A を採用する。** 現行の 3 層構成・DI コンテナ不使用・Service 抽象化なしを、
明示的な設計判断として固定する。

決め手：

- **規模との釣り合い** — ウィンドウ 1 枚・ViewModel 1 個・Service 3 個。抽象化が解く問題がまだ存在しない。
- **テスト容易性は別の手段で足りている** — 副作用と計算を分離し、計算部分を `internal static` にすれば
  モックなしでテストできる。実際 `BytesToFloats` / `CalculatePeak` / `IsSilent` がそうなっている。
- **読みやすさ** — DI コンテナの間接性は、規模が小さいうちは純粋にコストである。

## 結果

- **良くなること:** 「DI を入れるべきか」という議論が毎回発生しなくなる。
  構成が混在せず、コードの見た目が一定に保たれる。
- **悪くなること・受け入れるコスト:** ViewModel 全体のユニットテストは書けない
  （Service を差し替えられないため）。これは受け入れる。代わりに Service の純粋関数を厚くテストする。
- **後戻りのしやすさ:** 中程度。DI 導入は機械的な作業だが全 Service に及ぶ。
  [20-architecture-standards.md §6](../harness/20-architecture-standards.md#6-構造が壊れかけているサイン)
  の閾値（`MainViewModel.cs` が 1,500 行超、1 Service が 3 つ以上の無関係な外部リソースを扱う）に
  達したら、この ADR を置換する新しい ADR で見直す。

## 追随して更新するもの

- [x] `docs/harness/20-architecture-standards.md` — §2「意図的にやっていないこと」として反映済み
- [x] `CLAUDE.md` — アーキテクチャ方針から本 ADR を参照
- [ ] `docs/spec/02_architecture.md` — 現状記述として既に正しいため変更なし
