# T157 — 「中止」を押したことを画面で分かるようにする

> **状態:** 完了 — 2026-08-28
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ファイル文字起こしのオプション指定ダイアログで「中止」を押しても、話者識別の実行中は
見た目が何も変わらない。押せたのか効いていないのかを分かるようにする。

## 2. スコープ境界

**やること**
- 「中止」を押した直後にボタンを無効化する。
- 「中止を要求しました（…）」の 1 行をダイアログに出す。進捗表示とは**別の行**に置く。
- 文言を、話者ダイアライゼーションが動いているかで出し分ける。
- 仕様書 REQ-TRX-FILE-07 を改訂し、クラス図・シーケンス図に反映する。

**やらないこと（重要）**
- **中止がいつ効くかという判定ロジックは 1 行も変えない**（REQ-TRX-DIA-12 を維持）。
  `SpeakerDiarizationService.Process` の進捗コールバックは今までどおり常に `0` を返す。
- **`CancellationTokenSource` の生成・破棄・`Cancel()` の呼び出し順に触らない。**
- **出力ファイルの削除条件（REQ-TRX-FILE-07 の前半）に触らない。**
- **メインウィンドウ・ステータスバーには何も足さない**（中止の導線はダイアログだけ）。
- **T158 と同じブランチだが、`MainWindow.xaml` / `SettingsWindow.xaml` へは T157 として手を入れない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 注記をどこへ出すか | **進捗テキスト（`FileTranscriptionStatus`）とは別の `TextBlock`。** 同じ行へ書くと、次の `progress.Report` が上書きして 1 秒で消える（これが現象の直接の原因） |
| D2 | 「中止中...」の代入をどうするか | **`CancelFileTranscription` からは消す。** 上書きされて意味を成していない。進捗行はフェーズと処理済み時間を出し続けるほうが「まだ動いている」ことが伝わる |
| ~~D3~~ | 文言 | 話者識別が動いているとき **「中止を要求しました（話者識別が終わるまでお待ちください）」**、動いていないとき **「中止を要求しました（処理の切れ目までお待ちください）」**。起票時の文言は前者のみだったが、話者識別が無効な環境で「話者識別完了までお待ちください」と出すのは事実に反するため 2 通りにした |
| ~~D4~~ | 「動いている」の判定 | **`_speakerDiarizationService != null`。** これは `settings.json` の `SpeakerDiarizationEnabled` で起動時に決まり、途中で変わらない（`MainViewModel` のコンストラクター）。したがって注記は変化通知の要らない読み取り専用プロパティでよい |
| D5 | ボタンの無効化の実現 | **`CanCancelFileTranscription` に `&& !IsFileTranscriptionCancelRequested` を足す。** `Button` の `IsEnabled` を XAML から直に触らない（コマンドの実行可否は ViewModel が持つ） |
| D6 | フラグの寿命 | **`RequestFileTranscription`（ダイアログを開くとき）と `RunFileTranscriptionAsync`（開始時）の両方で `false` に戻す。** 同じダイアログで 2 回目を回す経路は無いが、戻し忘れると「開いた瞬間から中止済み」になるため両方で落とす |
| D7 | ✕ で閉じた場合 | **同じ経路を通る。** `Window_Closing` は `CancelFileTranscriptionCommand.Execute(null)` を呼ぶ。`RelayCommand.Execute` は `CanExecute` を見ないので、無効化されていても中止要求は通る（そのままウィンドウは閉じる） |

> **D3 / D4 は [T160](./T160-cancel-notice-by-phase.md) で差し替えた（2026-08-28）。**
> 「話者ダイアライゼーションが有効か」で文言を決めたのが誤りだった — 有効時は
> 「話者識別中」→「処理中」の 2 フェーズを通るため、Whisper 側で「中止」を押しても
> 「話者識別が終わるまでお待ちください」と出てしまう。現在は**押した時点のフェーズ**で決める。
> D1（進捗行とは別の行に置く）・D5（押した時点で無効化）・D6・D7 はそのまま有効である。

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-07 に「押したことが見て分かること」を追記
- [ ] `docs/spec/02_architecture.md` — 影響なし（層・スレッドモデル・データフローは変わらない）
- [x] `docs/spec/03_class_diagram.md` — `MainViewModel` に
      `IsFileTranscriptionCancelRequested` / `FileTranscriptionCancelNotice` を追加
- [x] `docs/spec/04_sequence_diagram.md` — §6 の「中止」の `alt` にフラグを立てる手順と、
      止まるのが推論の境界であることの注記を追加

## 5. アーキテクチャへの影響

- ADR: **不要。** `MainViewModel` にバインド用のプロパティが 2 つ増えるだけで、
  3 層構成・依存方向・スレッドモデルのいずれにも触れない。ダイアログは今までどおり
  `MainViewModel` を共有する状態レスな View である（ADR-0002）。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `IsFileTranscriptionCancelRequested` を追加。`FileTranscriptionCancelNotice` と `FileTranscriptionCancelNoticeFor` を追加。`CanCancelFileTranscription` に条件を追加。`CancelFileTranscription` でフラグを立てる（「中止中...」の代入は削除）。`RequestFileTranscription` / `RunFileTranscriptionAsync` でフラグを戻す |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | 進捗カードの中、状況テキストの下に注記の `TextBlock` を追加（`IsFileTranscriptionCancelRequested` で表示切替） |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `FileTranscriptionCancelNoticeFor` の 2 分岐のテストを追加 |
| `docs/spec/*` | §4 のとおり（済） |

## 7. 実装手順

### グループ A — ViewModel
- [ ] **A1** `IsFileTranscriptionCancelRequested` を `[ObservableProperty]` で追加し、
      `[NotifyCanExecuteChangedFor(nameof(CancelFileTranscriptionCommand))]` を付ける
- [ ] **A2** `internal static string FileTranscriptionCancelNoticeFor(bool diarizationActive)` を追加する
- [ ] **A3** `FileTranscriptionCancelNotice` を A2 に委譲する読み取り専用プロパティとして追加する
- [ ] **A4** `CanCancelFileTranscription` を `IsTranscribingFile && !IsFileTranscriptionCancelRequested` にする
- [ ] **A5** `CancelFileTranscription` でフラグを立て、`FileTranscriptionStatus = "中止中..."` を削除する
- [ ] **A6** `RequestFileTranscription` と `RunFileTranscriptionAsync` でフラグを `false` に戻す

### グループ B — ダイアログ
- [ ] **B1** 進捗カードの `FileTranscriptionStatus` の下に注記の `TextBlock` を足す (`FileTranscriptionOptionsWindow.xaml`)

### グループ C — テスト
- [ ] **C1** `FileTranscriptionCancelNoticeFor` の 2 分岐のテストを足す

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [ ] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`FileTranscriptionCancelNoticeFor_DiarizationActive_MentionsSpeakerIdentification`** —
  話者識別が動いているときの注記が「話者識別」に触れ、待たされることを伝えることを保証する。
- **`FileTranscriptionCancelNoticeFor_DiarizationInactive_DoesNotMentionSpeakerIdentification`** —
  話者識別が無効なときに「話者識別」と言わないことを保証する（D3 の理由そのもの）。

> **テストで守れない範囲:** 「押した直後にボタンが灰色になる」「注記が進捗の上書きで消えない」は
> WPF のバインディングとコマンドの `CanExecuteChanged` が実際に伝播した結果であり、
> ユニットテストでは検証できない。`CanCancelFileTranscription` の真偽そのものも、
> `IsTranscribingFile` を真にするには実際の文字起こしを走らせる必要があるためテストしない。
> ここは実機で「中止を押す → ボタンが無効化され、注記が出たまま進捗が進み続ける」を確認する。

## 9. 未解決の質問

なし。D3 だけが起票時の記述からの逸脱であり、理由を D3 に書いた。

## 10. 前提

- `_speakerDiarizationService` は `MainViewModel` のコンストラクターで一度だけ決まり、
  以後 `null` / 非 `null` が入れ替わらない（D4 が成り立つ根拠）。
- `progress.Report` の到着は `FileTranscriptionStatus` だけを書き換え、注記の `TextBlock` には
  触れない（D1 が成り立つ根拠）。
- `RelayCommand.Execute` は `CanExecute` を評価せずに実行本体を呼ぶ（D7 が成り立つ根拠）。

---

## 実行結果 (2026-08-28)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 242 件成功 / 0 件失敗 / 0 件スキップ（うち本タスクの追加は 2 件）
- 計画からの逸脱: なし。D3（文言を 2 通りにする）は起票時の記述からの意図的な逸脱であり、
  着手前に決定事項として固定した。
- **機械で確認できていない点:** 「押した直後にボタンが無効化される」「注記が進捗の上書きで
  消えない」は実機で目視する必要がある（§8 のとおり）。
