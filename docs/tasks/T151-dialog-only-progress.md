# T151 — ファイル文字起こしの進捗表示をダイアログ 1 か所にまとめる

> **状態:** 完了 — 2026-08-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ファイル文字起こしの進捗が、オプション指定ダイアログ（百分率つき）とメインウィンドウ
（不定プログレスバー）の 2 か所に出ていて二重になっている。メインウィンドウ側を削り、
**ダイアログ 1 か所に集約する。**

## 2. スコープ境界

**やること**
- メインウィンドウの進捗表示（不定プログレスバー＋状況テキスト）を削除する。
- メインウィンドウの「中止」ボタンも削除する（D2）。
- ダイアログを処理中に閉じようとしたら**確認したうえで中止する**（D1・D3）。
- REQ-TRX-FILE-06 / 07 / 13 を改訂する。

**やらないこと（重要）**
- **ダイアログ側の進捗表示は一切変えない。** 百分率つきの進捗バーとフェーズ表示はそのまま。
- **ステータスバーの `StatusMessage` は変えない。** 処理の開始・完了・中止の 1 行はそのまま出る。
- **`RunFileTranscriptionAsync` / `TranscribeFileAsync` の処理そのものに手を入れない。**
  中止の仕組みは既存の `CancellationTokenSource` をそのまま使う。
- **`FileTranscriptionStatus` プロパティは残す。** ダイアログが使っている。
- **`IsTranscribingFile` による他コントロールの排他（REQ-REC-09）は変えない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | REQ-TRX-FILE-13（ダイアログを閉じても処理継続）をどうするか | **廃止し、「閉じたら中止」へ改訂する**（利用者の指示 2026-08-23）。進捗を出す窓が閉じたのに処理だけ走り続ける状態を作らない |
| D2 | メインウィンドウの「中止」ボタン | **削除する**（利用者の指示 2026-08-23）。ダイアログはモーダルなので処理中はメインウィンドウを操作できず、閉じた瞬間に中止が走る。押せる場面が無いボタンを残さない |
| D3 | 閉じるときに確認を挟むか | **挟む。** `キャンセル` ボタンが `IsCancel="True"` のため **Esc でも閉じうる**。確認が無いと、長い文字起こしが誤操作で黙って消える。「いいえ」ならダイアログは閉じない |
| D4 | 中止の完了を待ってから閉じるか | **待たない。** 中止は sherpa-onnx の推論境界でしか効かない（REQ-TRX-DIA-12）ため、待つとダイアログが数十秒固まる。閉じてから中止が完了するまでの経過は**ステータスバーの 1 行**で分かる |
| D5 | 「音声ファイルから文字起こし」ボタンの表示切替 | **`IsTranscribingFile` で隠すのをやめ、`CanExecute` による無効化だけにする。** 隠す目的は「中止」ボタンと入れ替えることだったが、その相手が無くなったため。隠すと処理中に行が空になる |
| D6 | REQ-TRX-FILE-12（終わったら自動で閉じる） | **維持する。** 完了・失敗・中止のいずれでも閉じる挙動は変えない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - REQ-TRX-FILE-06 — 進捗の表示先を**ダイアログのみ**に改訂
  - REQ-TRX-FILE-07 — 「中止」の置き場を**ダイアログのみ**に改訂
  - REQ-TRX-FILE-13 — **「閉じても継続」から「閉じるなら確認して中止」へ改訂**
- [x] `docs/spec/03_class_diagram.md` — `FileTranscriptionOptionsWindow` に `Window_Closing` を追記
- [ ] `docs/spec/02_architecture.md` — 影響なし
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし（中止の呼び先は既存の `CancelFileTranscriptionCommand`）

## 5. アーキテクチャへの影響

- ADR: **不要。** ダイアログは引き続き `MainViewModel` を `DataContext` に共有する状態レスな View
  （ADR-0002）。閉じるときに ViewModel のコマンドを呼ぶだけで、自前の状態は持たない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/MainWindow.xaml` | 進捗表示の `StackPanel` と「中止」ボタンを削除。文字起こしボタンの表示切替を撤去 |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml.cs` | コンストラクタで `Closing` を結線し、処理中に閉じようとしたら確認して中止する（XAML は変更なし） |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 閉じる確認の文言を返す `internal static` を追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | その文言のテスト |
| `docs/spec/01_requirements.md` | REQ-TRX-FILE-06 / 07 / 13 を改訂 |
| `docs/spec/03_class_diagram.md` | 追加メソッドを反映 |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** REQ-TRX-FILE-06 / 07 / 13 を改訂 (`01_requirements.md`)
- [x] **A2** クラス図へ反映 (`03_class_diagram.md`)

### グループ B — ViewModel
- [x] **B1** `FileTranscriptionCloseConfirmation` を追加 (`MainViewModel.cs`)

### グループ C — View
- [x] **C1** メインウィンドウの進捗表示と「中止」ボタンを削除 (`MainWindow.xaml`)
- [x] **C2** ダイアログの `Closing` で確認して中止 (`FileTranscriptionOptionsWindow.xaml.cs`)

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`FileTranscriptionCloseConfirmation_Idle_ReturnsNull`** — 処理していなければ確認せずに閉じる
- **`FileTranscriptionCloseConfirmation_Transcribing_AsksToCancel`** — 処理中は中止の確認を出す

> **テストで守れない範囲:** XAML から要素が消えたこと、`Closing` の `e.Cancel`、
> Esc での閉じ方は WPF のウィンドウが要るため検証できない。**手動確認が要る。**

## 9. 未解決の質問

なし（D1〜D6 で確定）。

## 10. 前提

- ダイアログはモーダル（`ShowDialog`）であり、開いている間メインウィンドウは操作できない。
- 中止しても `IsTranscribingFile` はすぐには false にならない（推論境界まで待つ）。
  その間メインウィンドウの各操作は無効のままである（REQ-REC-09）。

---

## 実行結果 (2026-08-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 197 件成功 / 0 件失敗 / 0 件スキップ（うち T151 追加分 2 件）
- 計画からの逸脱: なし
- 手動確認が要る範囲: メインウィンドウから進捗表示と「中止」が消えていること、
  処理中にダイアログを × / Esc で閉じたときの確認と中止（§8 のとおりテストしていない）

### develop 取り込み後の再実行 (2026-08-25)

T149 (PR#35) / T150 (PR#36) のマージを取り込み、競合（台帳・要求仕様・クラス図・
`MainViewModel.cs`・テストの 5 ファイル）を解消したうえで再実行した。

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 217 件成功 / 0 件失敗 / 0 件スキップ（develop の 215 件 ＋ T151 の 2 件）

**要求仕様の競合は片方を捨てる判断をした。** REQ-TRX-FILE-13 は develop 側に旧版
（処理中にダイアログを閉じても処理は継続する）が入っていたが、本タスクはその要件を
廃止・改訂すること自体が目的なので、**本タスクの改訂版を採用し旧版を捨てた**。

あわせて、競合の解消だけでは辻褄が合わない 2 点を直した。

- **REQ-TRX-FILE-14（T149）に到達条件を追記した。** 改訂後の REQ-TRX-FILE-13 では
  ダイアログを閉じた時点で中止が始まるため、「ファイル文字起こし中にメインウィンドウの ×
  を押す」経路へ入れるのは**ダイアログを閉じた後、中止が完了しきる前の間だけ**になった。
  長い音声の推論境界待ち（REQ-TRX-DIA-12）では現実に起こりうるため要件自体は残し、
  条件だけを明記した
- クラス図の注記に `CloseConfirmationMessage`（T149）が抜けていたので加えた
