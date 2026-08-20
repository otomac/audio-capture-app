# T113 — ファイル文字起こしのオプション指定モーダルダイアログ

> **状態:** 完了 — 2026-08-20
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

音声ファイルからの文字起こしを、いきなり始めるのではなく **オプションを確認・指定してから**
始められるようにする。特に、録音の開始時刻（`hh:mm`）を与えて、出力される時刻を
「ファイル先頭からの経過時間」ではなく **実際の時刻** にできるようにする。

## 2. スコープ境界

**やること**
- ファイル選択直後（ドラッグ＆ドロップ含む）にモーダルダイアログを表示する。
- ダイアログに載せるもの: 対象ファイル名の表示 / 開始時刻 `hh:mm` の入力 / 進捗表示 / 中止。
- 指定した開始時刻を、出力行のタイムスタンプの起点にする。

**やらないこと（重要）**
- **ライブ文字起こし（録音中）側の時刻の扱いは変えない。** あちらは録音開始時刻の実時刻を
  既に持っている（REQ-TRX-LIVE-06）。
- **`MainWindow` 上の既存の進捗表示と「中止」ボタンを消さない。** ダイアログを ✕ で閉じても
  処理は続くため、メインウィンドウ側に中止手段が残っている必要がある。
- **日付は指定させない。** 時分のみ。`.txt` の書式は `hh:mm:ss` であり日付を持たない
  （REQ-TRX-07）。日付まで扱うなら書式ごと変える話になり、別タスク。
- **出力ファイル名・出力先は変えない**（REQ-TRX-FILE-05 のまま）。
- **新しい ViewModel を作らない。** [ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md) に従う。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 開始時刻の既定値 | **空欄（未指定）。** 未指定ならこれまでどおりファイル先頭を `00:00:00` として出力する。既存の挙動を既定に置く |
| D2 | 入力書式 | **`h:mm` / `hh:mm` の 24 時間表記のみ。** 秒は受け付けない（要求が `hh:mm`）。書式が不正なら「開始」を無効化し、その場で理由を表示する |
| D3 | オプション指定と進捗を 1 枚のダイアログでやるか、2 枚に分けるか | **1 枚。** 「開始」を押すと同じダイアログが進捗表示へ切り替わる。ウィンドウが開き直すと視線が飛ぶ |
| D4 | 処理が終わったときのダイアログの扱い | **自動的に閉じる**（完了・失敗・中止のいずれも）。結果はメインウィンドウのステータスバーに出る（REQ-OPEN-01 の導線もそこにある） |
| D5 | ダイアログを ✕ で閉じたら処理も止めるか | **止めない。** 処理は続き、メインウィンドウの進捗表示と「中止」ボタンが引き継ぐ（既存 UI をそのまま活かす） |
| D6 | 開始時刻 ＋ ファイル長が 24 時をまたいだら | **翌日の時刻として折り返す**（`23:00` 開始の 2 時間後は `01:00:00`）。`TimeSpan` の `hh` 書式が日数成分を含めないため自然にそうなる。壁時計として正しい |
| D7 | ダイアログの生成場所 | **`MainWindow` のコードビハインド。** ViewModel は `FileTranscriptionRequested` イベントで要求を上げるだけ（ADR-0002 の規則 2・3） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-09 〜 13 を追加。REQ-TRX-FILE-01 / 02 / 06 を改訂
- [x] `docs/spec/02_architecture.md` — View 層に `FileTranscriptionOptionsWindow` を追加
- [x] `docs/spec/03_class_diagram.md` — 新ウィンドウ、`MainViewModel` の新メンバー、
      `TranscribeFileAsync` のシグネチャ変更
- [x] `docs/spec/04_sequence_diagram.md` — §6 をダイアログ経由の流れへ書き換え

## 5. アーキテクチャへの影響

**あり。** ウィンドウが 1 枚から 2 枚（T114 と合わせて 3 枚）になり、ADR-0001 が決定の根拠に
挙げていた「ウィンドウ 1 枚」という前提が動く。
→ [ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md) を起票し、
補助ウィンドウは `MainViewModel` を共有する状態レスな View として追加すると決めた。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | 新規。ファイル名 / 開始時刻入力 / 進捗 / ボタン |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml.cs` | 新規。`MainViewModel` を受け取り、「開始」で処理を待って閉じる |
| `AudioCaptureApp/MainWindow.xaml.cs` | `FileTranscriptionRequested` を購読してダイアログを表示 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 要求イベント、ダイアログ用プロパティ、開始時刻の解析、進捗率 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TranscribeFileAsync` に `startOffset` を追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 開始時刻の解析と進捗率のテスト |

## 7. 実装手順

### グループ A — 時刻の起点
- [x] **A1** `TranscriptionService.TranscribeFileAsync` / `TranscribeFileCoreAsync` に
      `TimeSpan startOffset` を通し、`ProcessFileChunkAsync` へ `startOffset + chunkOffset` を渡す
- [x] **A2** 進捗レポートは従来どおりファイル先頭基準のまま（起点を足さない）

### グループ B — ViewModel
- [x] **B1** `TryParseStartTime` / `FileTranscriptionProgressFor` を `internal static` で追加
- [x] **B2** `FileTranscriptionRequested` イベントと、ファイル名・開始時刻・進捗率のプロパティを追加
- [x] **B3** `TranscribeFromFile` / `TranscribeDroppedFile` を「要求を上げるだけ」に変え、
      実処理は `StartFileTranscriptionAsync` へ移す

### グループ C — View
- [x] **C1** `FileTranscriptionOptionsWindow` を追加
- [x] **C2** `MainWindow` でイベントを購読しダイアログを表示

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`TryParseStartTime_Blank_ReturnsZero`** — 空欄は「未指定」として受理し、起点 0 になる
- **`TryParseStartTime_ValidForms_AreAccepted`** — `9:05` / `09:05` / `23:59` を受理する
- **`TryParseStartTime_InvalidForms_AreRejected`** — `24:00` / `9:5` / `12:60` / `12` /
  `12:34:56` を拒否する（拒否できないと不正な時刻で走り出す）
- **`FileTranscriptionProgress_ZeroTotal_ReturnsZero`** — 総時間 0 でゼロ除算しない
- **`FileTranscriptionProgress_HalfProcessed_ReturnsFifty`** — 進捗率が百分率で出る
- **`FileTranscriptionProgress_ProcessedExceedsTotal_ClampsTo100`** — 100 を超えない

> **テストで守れない範囲:** ダイアログの表示・モーダル性・自動クローズ・`Owner` による
> 連動クローズは WPF のウィンドウを必要とするため、ユニットテストでは検証できない
> （ハーネス §7 の方針どおり、境界の手前までを対象にする）。
> 「開始時刻がタイムスタンプに反映される」ことも Whisper の実行を伴うため未検証。
> 検証したのは `TranscribeFileAsync` が `startOffset` を受け取ることまで。

## 9. 未解決の質問

離席中のため、いずれも **既定案のまま確定** して実装した。戻ったら確認してほしい。

1. **開始時刻の既定値を「空欄」ではなく「ファイルの更新日時の時刻」にするか** —
   録音ファイルなら更新日時が録音終了時刻に近いことが多く、そこからファイル長を引けば
   開始時刻を推測できる。*採用しなかった。* 推測が外れたときに「勝手に入っていた値」を
   信じて出力してしまうリスクが、毎回入力する手間より重いと判断した。
2. **`.transcript.txt` に開始時刻の指定を記録するか** — 現状は記録しない。
   ファイルだけ見ても、その時刻がファイル先頭基準なのか実時刻なのか区別が付かない。
   ヘッダー行を足す案があるが、既存の行書式を読むツールを壊しうるため見送った。

## 10. 前提

- `TimeSpan` のカスタム書式 `hh` は日数成分を含まないため、24 時間を超えた分は自然に
  折り返す（D6 の根拠）。
- モーダルダイアログ表示中も WPF はメッセージポンプを回すため、`ShowDialog` の内側で
  `async` の継続が実行できる。

---

## 実行結果 (2026-08-20)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 118 件成功 / 0 件失敗 / 0 件スキップ
- 起動確認: `AudioCaptureApp.exe` を起動 → 8 秒後も稼働 → `CloseMainWindow()` で正常終了。
  BAML の解決失敗（`StaticResource` の未解決など）は起きていない
- 計画からの逸脱: なし

## 11. 積み残し・気づいたこと

- **既存の `MainWindow` 側の進捗表示は「不確定（`IsIndeterminate`）」のまま。**
  今回ダイアログには実際の進捗率（`FileTranscriptionProgress`）を出したが、メインウィンドウの
  `ProgressBar` は T111 以前からの不確定表示のままにしてある。統一するかは UI の判断が要るため
  触っていない。気になるようなら起票する。
- **ダイアログの実表示は手動確認していない。** 確認したのはアプリの起動と正常終了までで、
  ダイアログを実際に開いた状態（レイアウト・進捗の切り替わり・自動クローズ）は **未検証**。
- **ドラッグ＆ドロップからダイアログを開く経路も未検証。** ドロップ直後に
  モーダルを開くため、ドラッグ操作の終了処理と重なる。コード上は `Drop` ハンドラーが
  `e.Handled = true` を立ててから同期的に開くので問題ないはずだが、実機で確認していない。
- **`MainViewModel` が 900 行を超えた。** ADR-0002 が予告したとおり。
  分割の閾値（1,500 行）まではまだ距離があるが、T114 の後にもう一度見ること。
