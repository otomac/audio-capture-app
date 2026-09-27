# T180 — 配布物から未使用の `System.Net.Mail.dll` を外す

> **状態:** 進行中 — 2026-09-27
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

リリース版を起動するとウイルスバスターが「怪しいファイル」として起動確認を出し、その理由に
メールクライアント機能が挙がっている。アプリはメールを一切使っていないので、
self-contained 発行で .NET ランタイムと一緒に同梱されている `System.Net.Mail.dll` を配布物から外す。

## 2. スコープ境界

**やること**
- self-contained 発行時に `System.Net.Mail.dll` を発行物と `AudioCaptureApp.deps.json` の両方から除く
- 仕様書の技術スタック表に配布物の構成を書き、「`System.Net.Mail` を使わない」制約を残す

**やらないこと（重要）**
- **self-contained をやめる（framework-dependent 発行への切り替え）。** 利用者に .NET の導入を求めないという現状の配布方針は変えない
- **他の未使用ランタイム部品の削除・トリミング（`PublishTrimmed`）。** WPF はトリミング非対応（NETSDK1168）。今回はウイルスバスターの検出理由に挙がった部品だけを外す
- **コード署名・トレンドマイクロへの誤検知申告。** 警告の根本対策だが、証明書の調達を伴うので別判断

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | どこで外すか | **`.csproj` の MSBuild ターゲットで `ResolveRuntimePackAssets` の直後に `RuntimePackAsset` と `ReferenceCopyLocalPaths` の両方から除く。** `deps.json` は前者から、出力へのコピーは同ターゲットが前者を写した後者から作られる（前者だけ除くと DLL がコピーされ続けることを実測で確認）。CI で発行後にファイルを消すだけだと `deps.json` に記載が残り、起動時にホストが「依存アセットが見つからない」として止まる |
| D2 | 外して安全か | **安全。** アプリのコードに `System.Net.Mail` の使用は無い。同梱アセンブリを走査した結果、この DLL を参照するのは `System.dll` / `netstandard.dll`（型の転送のみ）と `DirectWriteForwarder.dll` / `System.Printing.dll`（参照の宣言のみで、型を 1 つも使っていない）だけ。CLR はアセンブリを型が要求された時点で遅延ロードするので、メールの型を使わない限り読み込まれない |
| D3 | どの発行に効かせるか | **self-contained 発行のみ**（`SelfContained == true`）。Debug ビルドやテストには影響させない |

## 4. 仕様書への影響

- [x] `docs/spec/02_architecture.md` — §1 技術スタック表に「配布」行を追加（self-contained / win-x64、`System.Net.Mail.dll` を除外、同名前空間は使用禁止）
- [-] `docs/spec/01_requirements.md` — 機能要件は変わらない
- [-] `docs/spec/03_class_diagram.md` — クラス構成は変わらない
- [-] `docs/spec/04_sequence_diagram.md` — 処理順序は変わらない

## 5. アーキテクチャへの影響

層構成・依存方向・スレッドモデルに触れない。ビルド構成のみの変更。

- ADR: **不要**（配布物から未使用部品を 1 つ除くだけで、設計判断の変更を伴わない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/AudioCaptureApp.csproj` | `ExcludeSystemNetMailFromRuntimePack` ターゲットを追加（`RuntimePackAsset` と `ReferenceCopyLocalPaths` の両方から除く） |
| `docs/spec/02_architecture.md` | §4 のとおり |
| `docs/tasks/backlog.md` | T180 を起票・完了 |

## 7. 実装手順

### グループ A — 除外
- [x] **A1** 変更前の発行物に `System.Net.Mail.dll` があり、`deps.json` にも記載されていることを確認する
- [x] **A2** `.csproj` に除外ターゲットを追加する
- [x] **A3** 発行し直し、DLL と `deps.json` の記載の両方が消えていることを確認する

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功（Windows の CI で確認する）
- [x] **Z4** 仕様書（§4）の更新反映を読み直す
- [ ] **Z5** CI の zip を Windows 実機で起動し、録音・文字起こしが動くこと、ウイルスバスターの警告の変化を確かめる（利用者）

## 8. テスト一覧

追加なし。ビルド構成の変更で、ユニットテストの対象になるコードは変えない。

> **テストで守れない範囲:** Windows 上での起動と、ウイルスバスターの判定が変わるかどうか。
> 後者は署名の無い新しい exe に対する評判判定も効いているとみられ、DLL を外しても警告が
> 残る可能性がある（残った場合はコード署名か誤検知申告を別タスクで検討する）。

## 9. 未解決の質問

なし。

## 10. 前提

- 将来 `System.Net.Mail` を使う機能を足す場合は、このターゲットを外す（使ったまま外すと実行時に `FileNotFoundException` になる）。

---

## 実行結果 (2026-09-27、Linux の作業環境で `EnableWindowsTargeting=true` を付けて実測)

- 変更前の発行物（self-contained / win-x64）: 280 ファイル。`System.Net.Mail.dll` あり、`deps.json` に記載あり
- 変更後の発行物: 279 ファイル。差分は `System.Net.Mail.dll` の 1 つだけ。`deps.json` の記載 0 件（JSON として正しく読める）
- 単一ファイル発行（README の手順）: 出力フォルダにも exe 内にも `System.Net.Mail.dll` なし
- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : **未実測。** Linux には `Microsoft.WindowsDesktop.App` ランタイムが無くテストホストが起動しない。Windows の CI（`build-desktop`）で確認する
- 計画からの逸脱: D1 の除外対象に `ReferenceCopyLocalPaths` を追加した（`RuntimePackAsset` だけでは `deps.json` からは消えるが DLL はコピーされ続けた）
