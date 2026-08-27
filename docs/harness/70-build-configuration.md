# 70 — ビルド構成とリポジトリ配置

品質ゲート（[40-quality-gates.md](./40-quality-gates.md)）を成立させている設定ファイルが
**どこに、なぜ置かれているか**。意図的な配置であり、偶然ではない。

---

## 1. リポジトリ配置

```
audio-capture-app/                    ← リポジトリルート ＝ .NET ソリューションルート
│  AudioCaptureApp.slnx               ソリューション（XML 形式。.sln ではない）
│  Directory.Build.props              全 .NET プロジェクト共通のビルド／解析設定
│  Directory.Packages.props           Central Package Management（バージョン一元管理）
│  .editorconfig                      root=true。書式・命名・ルール別 severity
│  CLAUDE.md                          常時ロードされるプロジェクトの法
├─ AudioCaptureApp/                   WPF 本体（net10.0-windows, WinExe）
│  ├─ Models/ ViewModels/ Services/ Controls/ assets/
├─ AudioCaptureApp.Tests/             xUnit テスト（net10.0-windows）
├─ docs/
│  ├─ harness/                        ← 規範（この文書群）
│  ├─ spec/                           現行仕様書（唯一の正）
│  ├─ tasks/                          タスク台帳と詳細
│  ├─ adr/                            アーキテクチャ決定記録
│  └─ archive/                        過去の成果物（参照専用）
├─ .claude/                           settings.json · hooks/
├─ .github/workflows/                 CI
├─ manual/                            利用者向けマニュアル
└─ models/ · publish/                 gitignore 対象（Whisper モデル／配布物）
```

## 2. なぜルートに置くのか

**共有設定は、それを共有するすべてのものの最も近い共通の親に置く（それより上には置かない）。**

参考にした .NET ハーネスは Next.js + .NET のモノレポで、.NET governance を `apps/api/` に置いていた。
本リポジトリは **.NET プロジェクトしか無い** ため、共通の親はリポジトリルートそのものになる。
したがって `Directory.Build.props` / `Directory.Packages.props` / `.editorconfig` はすべてルートに置く。

### MSBuild がこれを見つける仕組み

MSBuild はプロジェクトの場所からディレクトリ構造を **上へ辿り、最初に見つけた `Directory.Build.props`
で探索を止める**。`AudioCaptureApp/AudioCaptureApp.csproj` はルートまで上って本ファイルを適用する。
**ソリューションファイルの場所は、この探索に関係しない。**

> 出典: [Customize the build by folder](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)

### EditorConfig

EditorConfig も上へ辿り、`root = true` のファイルに当たるまで設定をマージする。
本リポジトリの `.editorconfig` は `root = true` なので、ここで完結する。
参考ハーネスのような 2 層構成（ルート＋`apps/api`）は、階層が 1 つしかない本リポジトリでは不要。

## 3. 決定 1 — 中間の `Directory.Build.props` は作らない

Microsoft のドキュメントには 3 階層（ルート＋`src`＋`test`）の例があるが、あれは
**マージの仕組みを説明するための例** であり、文言も「望ましい *かもしれない*」と条件付きである。

**原則：内側の `Directory.Build.props` は、その階層に親と本当に異なる設定があるときだけ作る。**
本プロジェクトはテストプロジェクトも同じベースラインでよく、差分は `.editorconfig` の
パススコープセクションで足りる。よって **作らない**。

## 4. 決定 2 — テストの差分は `.editorconfig` で吸収する

テストは「ベースライン − いくつかのルール」であり、**除外ではなく上書き** の関係にある。

```ini
[AudioCaptureApp.Tests/**.cs]
# CA1707: テストメソッド名は 対象_条件_期待結果 形式で _ を含む — 2026-08-14
dotnet_diagnostic.CA1707.severity = none
```

`[*.cs]` セクションより **後ろ** に書くため、テストファイルではこちらが勝つ。追加ファイルは要らない。

将来、テスト専用の **MSBuild プロパティ** が必要になったら、そのときに
`AudioCaptureApp.Tests/Directory.Build.props` を作り、上位の探索を再開させる import を書く：

```xml
<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
        Condition="'' != $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
```

（MSBuild は最初に見つけたファイルで探索を止めるため、この import が無いとルートの設定が効かなくなる。）

## 5. 決定 3 — `Directory.Packages.props` は 1 つだけ

Central Package Management（CPM）は階層ごとに分割しない。NuGet は複数ある場合
**プロジェクトに最も近い 1 ファイルだけを評価し、マージしない**。分割すると一部プロジェクトが
一元管理から外れてしまう。よって **テスト専用パッケージを含む全バージョンをルートの 1 ファイル** に置き、
`.csproj` 側は名前のみで参照する（`Version` 属性を書かない）。

> 出典: [Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)

CPM 導入前はテストプロジェクトが本体より古いバージョン（xunit 2.5.3 / Test.Sdk 17.8.0 等）を
参照しており、意図しないバージョンドリフトが起きていた。CPM はこれを構造的に防ぐ。

## 6. 決定 4 — ソリューションは `.slnx`

本リポジトリは XML 形式の `.slnx` を採用している（`.sln` からの移行済み）。

- ビルド／テスト／format のコマンドはすべて `AudioCaptureApp.slnx` を対象にする。
- **`.sln` を参照するコマンドやスクリプトを書かないこと。** 存在しないファイルを指すため失敗する。
- `.slnx` の読み込みには .NET SDK 9 以降が必要。ターゲットが `net10.0-windows` のため CI は
  `DOTNET_VERSION: 10.0.x` を使う。これを下げないこと。

## 7. パッケージを追加するとき

1. **承認を得る**（[00-ways-of-working.md](./00-ways-of-working.md#ライブラリ追加は個別承認制)）。
2. `Directory.Packages.props` に `<PackageVersion Include="..." Version="..." />` を追加する。
3. `.csproj` に `<PackageReference Include="..." />` を **`Version` 属性なしで** 追加する。
4. `docs/spec/02_architecture.md` の技術スタック表を更新する（法 2）。
5. 品質ゲートを回す。新しいアナライザーを含むパッケージなら警告が増える可能性がある。
