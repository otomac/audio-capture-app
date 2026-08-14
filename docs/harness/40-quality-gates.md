# 40 — 品質ゲート

**法 4 の本体。** 3 つのゲートをすべてクリアするまで、作業は「完了」ではない。

---

## 1. ゲートの定義

作業の最後（[10-workflow.md](./10-workflow.md) の S6）で、**3 つとも順に実行する**。

| # | ゲート | コマンド | 合格条件 |
|---|---|---|---|
| **G1** | ビルド＋静的解析 | `dotnet build AudioCaptureApp.slnx -c Debug` | 警告 0 件・エラー 0 件 |
| **G2** | 書式・スタイル | `dotnet format AudioCaptureApp.slnx --verify-no-changes` | 差分なし（終了コード 0） |
| **G3** | ユニットテスト | `dotnet test AudioCaptureApp.slnx -c Debug` | 全件成功・失敗 0 件 |

まとめて実行する場合（PowerShell、途中で落ちたら止まる）:

```powershell
dotnet build AudioCaptureApp.slnx -c Debug `
  && dotnet format AudioCaptureApp.slnx --verify-no-changes `
  && dotnet test AudioCaptureApp.slnx -c Debug
```

G2 が落ちたときは `--verify-no-changes` を外して実行すれば自動修正される:

```powershell
dotnet format AudioCaptureApp.slnx
```

### G1 が「警告 0 件」を意味する理由

[Directory.Build.props](../../Directory.Build.props) で以下を設定している。

| プロパティ | 効果 |
|---|---|
| `AnalysisMode=All` | .NET 組み込みアナライザーの **全ルール** を有効化 |
| `AnalysisLevel=latest` | SDK が持つ最新のルールセットを使う |
| `EnforceCodeStyleInBuild=true` | `IDE*` のスタイルルールを **IDE だけでなくビルド時にも** 走らせる |
| `TreatWarningsAsErrors=true` | 警告を **エラー** にする ← これが歯 |
| `CodeAnalysisTreatWarningsAsErrors=true` | アナライザー警告もエラーにする |
| `Nullable=enable` | null 許容参照型の警告を有効化 |

つまり **警告が 1 件でも出ればビルドが失敗する**。「警告はあるが動く」という状態は存在しない。

### なぜ G2 が別途必要か（G1 だけでは足りない）

**`dotnet build` は `EnforceCodeStyleInBuild=true` を付けても `IDE1006`（命名規則）を報告しない。
`dotnet format` は報告する。** これは本プロジェクトで実測した事実である
（[T100](../tasks/T100-dev-harness.md) の実行結果参照）— 命名ルールを追加した直後、
ビルドは 0 警告 / 0 エラーだったのに `dotnet format --verify-no-changes` は 5 ファイルで
`IDE1006` を検出した。

参考にした .NET ハーネスは Serena（Roslyn LSP）の診断でこの穴を埋めていた。
本プロジェクトは Serena を使わないため、**G2 がその役割を担う**。
「ビルドが通ったからよい」で G2 を飛ばさないこと。

### 絶対禁止

- **アナライザー・テストを無効化してゲートを通すこと。** 原因を直す。
- **実行せずに「通ります」と書くこと。** 出力を確認してから書く。
- **件数を伏せた報告。** 「テストが通る」ではなく「42 件成功 / 0 件失敗 / 0 件スキップ」と書く。

---

## 2. CI での強制

[.github/workflows/build-desktop.yml](../../.github/workflows/build-desktop.yml) が
`main` への push / PR / 手動実行で G1〜G3 と同じゲートを実行する。
ローカルと CI で **同じコマンド・同じ設定** が走るようにしてある（設定はすべて
`Directory.Build.props` と `.editorconfig` にあり、CI 側でフラグを上書きしない）。

CI だけが落ちる／ローカルだけが落ちる状態は **バグ** である。見つけたら起票して原因を消す。

---

## 3. 抑止の書き方

アナライザーが **本当に誤検知** している場合に限り抑止してよい。範囲は最小に、理由は必ず添える。

### 3-1. 局所的な抑止（推奨）

その 1 箇所だけの問題なら、コードの側で抑止する。

```csharp
// CA1031: NAudio のコールバック内で例外を漏らすとプロセスが落ちるため、
//         ここは全例外を捕捉して RecordingError イベントに変換する。
#pragma warning disable CA1031
catch (Exception ex)
{
    RecordingError?.Invoke(this, ex);
}
#pragma warning restore CA1031
```

または `[SuppressMessage]` 属性に `Justification` を書く。

### 3-2. プロジェクト全体の緩和（例外的）

ルールそのものが本プロジェクトの前提に合わない場合に限る。
[.editorconfig](../../.editorconfig) の末尾セクションに、**理由と日付を 1 行** 添えて書く。

```ini
# CA1234: <本プロジェクトで不適な理由> — YYYY-MM-DD
dotnet_diagnostic.CA1234.severity = suggestion
```

理由コメントのない severity 変更は差し戻し対象。

---

## 4. 既知の穴

ゲートは完全ではない。以下はツールが取りこぼすので、**レビューで担保** する。

| 穴 | 内容 | 担保 |
|---|---|---|
| **`Async` サフィックス** | EditorConfig の命名ルールは戻り値型を判別できず `async` 修飾子にしか反応しない。`async` なしで `Task` を返すメソッドを検出できず、逆に WPF の `async void` イベントハンドラーを誤検出する。このためこのルールだけ `suggestion` にしてある | 手で正しく命名する（[30-coding-standards.md §4](./30-coding-standards.md#4-命名editorconfig-で強制)） |
| **スレッド規律** | Dispatcher 経由か、コールバック内でブロックしていないかはアナライザーで検出できない | [20-architecture-standards.md §3](./20-architecture-standards.md#3-スレッドモデルの規範) をレビュー観点にする |
| **アーキテクチャの依存方向** | 単一プロジェクト構成のため、層をまたぐ参照をビルドで止められない | [20-architecture-standards.md §1](./20-architecture-standards.md#1-レイヤーと依存方向) をレビュー観点にする |
| **実機依存の挙動** | オーディオデバイス・Whisper モデル実体を要求する経路はユニットテストの対象外 | 手動確認。境界の手前（純粋関数）までを G3 で守る |
| **カバレッジ** | 閾値ゲートは **設けていない**。CI は cobertura を収集するのみ | 数値は参考情報。カバレッジのために無意味なテストを書かない |

---

## 5. 技術的負債（期限付き緩和）

ハーネス導入時点（2026-08-14）で、既存コードに `AnalysisMode=All` 由来の警告が
**実効約 85 件** あった。ハーネス構築とソース修正を混ぜないため、以下は
**一時的に `suggestion` へ落とし、解消タスクを起票** している。

> **新規コードでは最初から守ること。** 緩和は既存コードの猶予であって、免罪符ではない。

| ルール | 内容 | 解消タスク |
|---|---|---|
| `CA1063` / `CA1816` / `CA1001` / `CA2213` / `CA2000` | `Dispose` パターンの実装漏れ、破棄されないフィールド／ローカル | `T101` |
| `CA1305` / `CA1307` | カルチャー／`StringComparison` の未指定 | `T102` |
| `CA1031` | `catch (Exception)` の広すぎる捕捉 | `T103` |
| `CA1849` / `CA2016` | 非同期メソッド内での同期呼び出し、`CancellationToken` の非伝播 | `T104` |
| `CA1822` / `CA1825` | `static` にできるメンバー、空配列の再確保 | `T105` |

進捗は [docs/tasks/backlog.md](../tasks/backlog.md) で管理する。
解消したルールは **`.editorconfig` の負債セクションから行ごと削除** すること（緩和を残さない）。

### 恒久的な緩和（負債ではない）

以下は本プロジェクトの前提に合わないと **判断済み** のルール。解消タスクは無い。

| ルール | 緩和理由 |
|---|---|
| `CA2007`（`ConfigureAwait` を付けよ） | WPF では継続を UI コンテキストに戻す必要があり、付けると誤動作する |
| `CA1515`（型を `internal` にせよ） | 単体実行ファイルであり、公開 API の縮小に意味がない |
| `CA1003`（`EventHandler<T>` を使え） | NAudio 由来のイベント形状に合わせる必要がある |
| `CA1024`（メソッドをプロパティにせよ） | 副作用と計算コストを踏まえた API 設計判断であり、機械的に従わない |
| `CA1062`（公開引数を null 検証せよ） | 外部公開ライブラリではなく、単体アプリの内部呼び出しに対して過剰 |
| `CA1707`（識別子に `_` を含めるな）※テストのみ | テスト名は `対象_条件_期待結果` 形式を採用している |

---

## 6. 将来の強化候補（未導入）

以下は参考ハーネスにあるが、既存コードへの影響が大きいため **未導入**。
導入するときはタスクを起票し、警告数を実測してから判断する。

- **Meziantou.Analyzer**（性能・正しさ）
- **SonarAnalyzer.CSharp**（コードスメル・バグパターン）

[Directory.Packages.props](../../Directory.Packages.props) にコメントで導入手順を残してある。
