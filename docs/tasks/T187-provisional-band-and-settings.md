# T187 — 速報の表示（字幕帯）と設定 UI

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **設計:** [T181](./T181-two-pass-live-transcription-design.md) D6 / D15 / D16、[ADR-0007](../adr/0007-two-pass-live-transcription.md) 決定 3、[ADR-0008](../adr/0008-per-window-viewmodels.md)（ウィンドウごとの ViewModel）

## 1. 目的

T186 の速報を画面に出す。文字起こしウィンドウの行一覧の下に高さ固定の字幕帯を置き（REQ-LIVEVIEW-10）、
設定ウィンドウで速報の有効化と速報用モデルを選べるようにし（REQ-SETWIN-03 ⑦）、モデル管理の名前変更・削除に追従させる（REQ-MODELWIN-03 / 04）。

## 2. スコープ境界

**やること**
- `LiveTranscriptViewModel` に速報の一覧（最大 4 件）と、速報・確定完了の通知の反映（純粋関数）
- `LiveTranscriptWindow.xaml` に字幕帯（行一覧の下・高さ固定・薄い斜体）
- 録音開始で字幕帯をクリアしてセッションを受け付け、停止の完了で空にする（REQ-LIVEVIEW-08 / REQ-TRX-LIVE-11）
- 設定ウィンドウに「速報」— 有効化のチェックボックス・速報用モデルのドロップダウン・状態表示
- モデル管理の名前変更・削除・並び替えで速報用の選択を追従させる

**やらないこと（重要）**
- **行一覧（`LiveTranscriptLines`）とそのスクロール処理（`LiveTranscriptWindow.OnLinesChanged`）は変えない**（REQ-LIVEVIEW-03 / 04 / 06 / 07 / 09）
- **用語集（⑧）は T188**
- **ウィンドウの初期サイズ（480x240）は変えない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 速報の一覧の持ち方 | `ProvisionalEntry`（音源・句 ID・範囲・表示文字列）の並び（届いた順、最大 4 件）。表示用の `ObservableCollection<string> ProvisionalLines` はそれに合わせて置き換える。反映の規則は `internal static` の純粋関数にしてテストする |
| D2 | 反映の規則 | 速報: セッション ID が違えば捨てる／その音源の確定位置以下で終わる速報は捨てる／同じ音源・句 ID は**その場で上書き**、新しい句は末尾に足し、4 件を超えたら古い方から捨てる。確定完了: セッション ID が違えば捨てる／その音源の確定位置を進め、**終端が確定位置以下の速報を消す**（確定位置をまたぐ速報は残す） |
| D3 | 受け付けるセッション | 録音の開始に成功したら、`TranscriptionService.CurrentSessionId` を受け付ける ID にする（`BeginProvisionalSession`）。停止の完了で字幕帯を空にし、どのセッションも受け付けない（`EndProvisionalSession`）。ライブ文字起こしが OFF なら通知は来ないので、前の ID のままでも害は無い |
| D4 | 表示の形 | 1 件を `[ラベル] 本文` の 1 項目にし、折り返す。字幕帯は高さ固定（4 行分）で、**下端に揃えて**はみ出した古い行を上で切る（最新の文字が常に見える）。下端に揃えるのは `Canvas.Bottom`（`VerticalAlignment="Bottom"` は中身が枠より高いと効かず、最新の行が下で切れた。Z5 の画像で見つけた）文字は 9pt・斜体・`Brush.TextMuted` |
| D5 | 設定の UI | 設定ウィンドウの「文字起こし」カードに「速報（字幕帯）」の小見出しを置き、チェックボックス「録音中に速報を出す」・ドロップダウン「速報用モデル」（登録一覧と同じもの。推奨は base の注記）・状態表示（`ProvisionalModelStatus`）を並べる。未選択の選択肢は置かない — 速報を止めたいときはチェックボックスで止める |
| D6 | 変更の反映 | どちらも変えた時点で保存し（REQ-CFG-05）、速報用モデルを読み直す（同じパスなら読み直さない。T186 D9）。有効化の切り替えはサービスの `ProvisionalEnabled` にも写す（次の録音から効く） |
| D7 | 読み込みの直列化 | 速報用モデルだけを読み直す経路（`ReloadProvisionalModel`）を足す。ライブ用の読み込み中・速報用の読み込み中に来た要求は「もう 1 回」の印だけ付け、走っている読み込みが最後に読み直す。ライブ用の読み込みは、走っている速報用の読み込みが終わるのを待ってから始める（ネイティブログの観測を重ねない。REQ-TRX-04） |
| D8 | 名前の変更・削除・並び替え | 名前の変更: 速報用に選ばれていれば新しい要素へ付け替える（パスは同じなので読み直さない）。削除: 速報用に選ばれていれば選択を外し、**速報を無効に倒す**（REQ-MODELWIN-04）。並び替え: 選択を変えない。いずれも ComboBox が選択を一時的に `null` にしうるので、ライブ用と同じ抑止（`ChangeWhisperModelsWithoutWriteBack`）の中で行う |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-LIVEVIEW-02 / 08 / 10、REQ-TRX-LIVE-11（字幕帯を空にする）、REQ-SETWIN-03 ⑦、REQ-MODELWIN-03 / 04 / 08、REQ-CFG-12（設定ウィンドウの項目）の実装箇所と「未実装」の除去
- [x] `docs/spec/02_architecture.md` — §5.2.1 の「字幕帯は未実装」を外す
- [x] `docs/spec/03_class_diagram.md` — `LiveTranscriptViewModel` / `SettingsViewModel` の新メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §2 録音開始（字幕帯のクリアとセッション）、§4 停止（字幕帯を空にする）

## 5. アーキテクチャへの影響

- ADR: 不要 — 字幕帯は文字起こしウィンドウの ViewModel（ADR-0008）に置き、通知は既存の `SegmentTranscribed` と同じく Service のイベント → Dispatcher で受ける。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/LiveTranscriptViewModel.cs` | 速報の一覧・通知の受け口・純粋関数 |
| `AudioCaptureApp/LiveTranscriptWindow.xaml` | 字幕帯 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 通知の購読・設定の保存 |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs` | 録音開始でセッションを受け付け、停止の完了で空にする |
| `AudioCaptureApp/ViewModels/SettingsViewModel.cs` | ライブ用の読み込みの前に速報用の読み直しを待つ・初期値 |
| `AudioCaptureApp/ViewModels/SettingsViewModel.Provisional.cs` | 新規（partial）。速報の有効化・速報用モデル・読み直しの直列化（`SettingsViewModel.cs` が 500 行の目安を超えたため分けた） |
| `AudioCaptureApp/ViewModels/WhisperModelsViewModel.cs` | 名前の変更・削除・並び替えの追従 |
| `AudioCaptureApp/SettingsWindow.xaml` | 速報の項目 |
| `AudioCaptureApp.Tests/LiveTranscriptProvisionalTests.cs` | 新規 |

## 7. 実装手順

### グループ A — 字幕帯
- [x] **A1** `LiveTranscriptViewModel` の速報の一覧と純粋関数
- [x] **A2** `LiveTranscriptWindow.xaml` の字幕帯
- [x] **A3** `MainViewModel` の購読、録音開始・停止の完了での受け付けとクリア

### グループ B — 設定 UI
- [x] **B1** `SettingsViewModel` の有効化・速報用モデル・読み直し
- [x] **B2** `SettingsWindow.xaml` の項目
- [x] **B3** `WhisperModelsViewModel` の追従

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す
- [x] **Z5** 同一プロセスの UI 検証で、字幕帯と設定ウィンドウの XAML が実行時に落ちないこと・字幕帯の見え方を画像で確かめる

## 8. テスト一覧

- **`ApplyProvisionalUpdate_SamePhrase_OverwritesInPlace`** — 同じ句の更新は上書き（並びは変えない）
- **`ApplyProvisionalUpdate_NewPhrase_AppendsAndKeepsAtMostFour`** — 新しい句は末尾、5 件目で最古を捨てる
- **`ApplyProvisionalUpdate_OtherSession_IsIgnored`** / **`ApplyProvisionalUpdate_NoSession_IsIgnored`** — 前のセッション・受け付けていないときの通知は捨てる
- **`ApplyProvisionalUpdate_AlreadyConfirmed_IsIgnored`** — 確定位置以下で終わる速報は出さない
- **`ApplyChunkCompletion_RemovesConfirmedAndKeepsStraddling`** — 確定位置までの速報を消し、またぐ速報は残す
- **`ApplyChunkCompletion_OtherSource_IsUntouched`** — 別の音源の速報は消さない
- **`ApplyChunkCompletion_OtherSession_IsIgnored`**
- **`ProvisionalDisplay_HasLabelAndText`** — 表示の形
- **`SyncLines_*`** — 表示用コレクションを並びどおりに置き換える

> **テストで守れない範囲:** 字幕帯の見え方（下揃え・クリップ）、設定ウィンドウの操作。Z5 の画像で確かめる。
> モデル管理の追従は WPF の ComboBox の挙動に依存するため、Z5 の同一プロセス検証で操作して確かめる。

## 9. 未解決の質問

なし（D4 の表示の形・D5 の UI の置き方は既定案で決めた）。

## 10. 前提

- T186 の `ProvisionalTranscribed` と T185 の `LiveChunkCompleted` が入っていること。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 472 件成功 / 0 件失敗 / 0 件スキップ（T186 完了時 459 件 + 本タスク 13 件）
- **Z5 同一プロセスの UI 検証**（スクラッチ `uicheck`。本体の `App` のリソースで `LiveTranscriptWindow` / `SettingsWindow` / `WhisperModelsWindow` を開く。`settings.json` は退避 → 試験用に差し替え → 復元し、バイト一致を確かめた）: 27 項目すべて OK
  - 字幕帯: 前のセッションの通知を捨てる・最大 4 件・最新が末尾・確定位置までの速報を消す・停止の完了で空になり以後を受け付けない・行一覧は変わらない
  - 起動時にライブ用（small・GPU）の後で速報用（base・CPU）を読み込む
  - モデル管理を開いた状態で: 並び替えで選択が変わらない／名前の変更に追従し保存名も変わる（破棄しない）／別の要素の名前変更では変わらない／削除で選択を外し速報を無効に倒して破棄する／選び直すと読み込む
  - 画像で確かめた: 字幕帯は下端に揃い、はみ出した古い行が上で切れる。設定ウィンドウの「速報（字幕帯）」の並び
- 計画からの逸脱:
  - D4 の実装を `Canvas.Bottom` に変えた（`VerticalAlignment="Bottom"` では最新の行が切れることを画像で見つけた）
  - `SettingsViewModel.cs` が 534 行になったため、速報の部分を `SettingsViewModel.Provisional.cs`（partial）へ分けた（中身は変えていない）
- 未確認: 実際の録音（マイク・スピーカーのキャプチャ）を通した画面での見え方。サービスの通しは T186 で確かめた。利用者の手元での確認を勧める
