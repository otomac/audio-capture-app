# T111 — 文字起こし完了時に保存先フォルダを開けるようにする

> **状態:** 完了 — 2026-08-16
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

リアルタイム録音および音声ファイルからの文字起こしを完了した際に、
成果物が置かれたフォルダをアプリから開けるようにする（要件 3）。

## 2. スコープ境界

**やること**

- 直近の成果物のフォルダを開くコマンドとボタンを追加する
- エクスプローラーで**成果物ファイルを選択した状態**で開く

**やらないこと（重要）**

- **保存先フォルダの変更・設定の追加。** 既存の `OutputFolder` の扱いは変えない。
- **成果物の一覧・履歴機能。** 保持するのは「直近 1 件」のみ。
- **`.txt` / `.mp3` を開く（関連付け起動）。** 開くのはフォルダまで。
- **ファイル文字起こしの出力先の変更。** 引き続き入力ファイルと同じフォルダに出す。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | どのフォルダを開くか | **直近の成果物が置かれたフォルダ。** 録音は `OutputFolder`、ファイル文字起こしは入力ファイルと同じフォルダで、両者は一致しないため「設定の保存先」ではなく「直近の成果物の場所」を開く |
| D2 | ボタンをいつ有効にするか | **直近の成果物パスが設定されているとき。** 起動直後は無効 |
| D3 | 録音のみ（ライブ文字起こし OFF）でも有効にするか | **する。** 成果物は MP3 として存在する。ここで無効にすると「録音したのにボタンが押せない」という不可解な状態になる |
| D4 | 開き方 | **`explorer.exe /select,"<path>"` で成果物を選択状態にする。** フォルダを開くだけより目的のファイルに辿り着きやすい |
| D5 | 成果物が消えていた場合 | **親フォルダを開く。** 親も無ければステータスに通知して何もしない |
| D6 | ボタンの位置 | **ステータスバー右端。** 成果物・ステータスと文脈が近く、既存レイアウトへの影響が最小 |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — 新セクション「11. 成果物フォルダを開く」に REQ-OPEN-01〜04 を新設
- [x] `docs/spec/03_class_diagram.md` — `MainViewModel` のコマンド／プロパティ追加
- [-] `docs/spec/02_architecture.md` — 層構成・依存方向は不変
- [-] `docs/spec/04_sequence_diagram.md` — 既存の処理順序は変えない

## 5. アーキテクチャへの影響

3 層構成・依存方向とも不変。外部プロセス起動を ViewModel から行うが、
既存の `Microsoft.Win32.OpenFolderDialog` / `OpenFileDialog` の直接利用と同じ扱いで、
新たな抽象化レイヤーは設けない（ADR-0001 の方針に沿う）。

- ADR: **不要**

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `LastResultPath` プロパティ、`OpenResultFolderCommand`、`BuildExplorerArguments` |
| `AudioCaptureApp/MainWindow.xaml` | ステータスバーに「保存先を開く」ボタンを追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `BuildExplorerArguments` のテスト |
| `docs/spec/01_requirements.md` | §4 のとおり |
| `docs/spec/03_class_diagram.md` | 同上 |

## 7. 実装手順

- [x] **A1** `BuildExplorerArguments` を純粋関数として追加（ファイル有無で分岐）
- [x] **A2** `LastResultPath` と `OpenResultFolderCommand` を追加
- [x] **A3** `StopRecordingAsync` の完了時に `LastResultPath` を設定
- [x] **A4** `RunFileTranscriptionAsync` の成功時に `LastResultPath` を設定
- [x] **B1** ステータスバーにボタンを追加 (`MainWindow.xaml`)
- [x] **C1** テストを追加
- [x] **Z1** `dotnet build` — 警告 0 件
- [x] **Z2** `dotnet format --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test` — 全件成功
- [x] **Z4** 仕様書の更新反映を読み直す

## 8. テスト一覧

- **`BuildExplorerArguments_ExistingFile_SelectsFile`** — 実在ファイルは `/select,"..."` になる
- **`BuildExplorerArguments_MissingFileButExistingFolder_OpensFolder`** — ファイルが消えていても親フォルダを開く
- **`BuildExplorerArguments_MissingFileAndFolder_ReturnsNull`** — どちらも無ければ何もしない
- **`BuildExplorerArguments_EmptyPath_ReturnsNull`** — 未設定時

> **テストで守れない範囲:** `explorer.exe` が実際に起動してフォルダが開くことは
> ユニットテストで検証できない（手動確認）。テストは引数の組み立てまでを担保する。

## 9. 未解決の質問

なし（§3 の決定で判断した）。

## 10. 前提

- 成果物のパスは `AudioCaptureService.CurrentSession.FilePath`（録音）と
  `TranscriptionService.BuildTranscriptPath`（ファイル文字起こし）から得られる。
- Windows 専用アプリのため `explorer.exe` の存在を前提にしてよい。

---

## 実行結果 (2026-08-16)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **58** 件成功 / **0** 件失敗 / **0** 件スキップ
  （既存 53 件 ＋ 新規 5 件）
- 計画からの逸脱: なし

### 手動確認をお願いしたいこと

`explorer.exe` の起動そのものはユニットテストで検証できないため（引数の組み立てまでが担保範囲）、
以下は手動確認が必要。**検証環境でエクスプローラーのウィンドウを開くのは副作用が大きいため
実行していない。**

1. 起動直後は「保存先を開く」が**無効**になっている
2. ライブ文字起こし ON で録音 → 停止 → ボタン押下で **`.txt` が選択された状態**でフォルダが開く
3. ライブ文字起こし OFF で録音 → 停止 → ボタン押下で **`.mp3` が選択された状態**で開く
4. 音声ファイルから文字起こし → 完了 → ボタン押下で **入力ファイルと同じフォルダ**が開き
   `.transcript.txt` が選択される
5. 成果物を手動で削除してからボタン押下 → **親フォルダが開く**（エラーにならない）
6. 録音データなし（無音のまま停止して MP3 が削除された場合）→ ボタンが**無効のまま**
