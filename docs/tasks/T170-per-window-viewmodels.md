# T170 — `MainViewModel` をウィンドウごとの ViewModel に分ける

> **状態:** 完了 — 2026-09-29（画面の操作確認は利用者の手元で行う。§8）
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **根拠:** [ADR-0008](../adr/0008-per-window-viewmodels.md)（承認済み・2026-09-29）

## 1. 目的

[ADR-0006](../adr/0006-mainviewmodel-split-reevaluation.md) が期限を切った「案 A（ウィンドウ単位の ViewModel）の是非」を判断し、
利用者の選択（2026-09-29: 「ウィンドウごとの ViewModel」「ADR と実装まで」）に従って **機能を 1 つも足さずに** 分割する。
ライブ文字起こしの 2 段化（T187 / T188）で ViewModel に手を入れる前に、置き場所を画面の単位で決めておく。

## 2. スコープ境界

**やること**

- ADR-0008 に判断を書く（ADR-0002 / 0005 / 0006 の該当規則を置き換える）
- 補助ウィンドウ 5 枚に ViewModel を 1 つずつ作り、`MainViewModel` の対応するメンバーを移す
- 補助ウィンドウのコードビハインドが受け取る型を、それぞれの ViewModel に変える
- テストの静的メンバーの呼び先を、移った先のクラスに直す
- 規範（`CLAUDE.md`・`docs/harness/`）と仕様（`docs/spec/01〜04`）の記述を合わせる

**やらないこと（重要）**

- **挙動を変えない。** 表示する文言・ボタンの可否の条件・保存するタイミング・処理の順序はそのまま。
- **XAML を変えない。** バインド名は転送プロパティで同じ名前を出す（ADR-0008 規則 5）。
- **テストの中身を変えない。** 直すのは呼び先のクラス名だけ（§8）。
- **ついでの修正をしない。** 気づいた問題は別タスクにする。
- **DI コンテナ・メッセンジャー・Service のインターフェースを入れない**（ADR-0001 を維持）。
- **ライブラリを足さない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 分割の方式 | **案 A（ウィンドウ 1 枚に ViewModel 1 つ）。** 利用者の選択（2026-09-29）。ADR-0008 |
| D2 | 子の生成と寿命 | **親（`MainViewModel`）が 1 度だけ生成し、アプリの終了まで保持する。** 子はコンストラクターで親を受け取る。`WhisperModelsViewModel` だけは `SettingsViewModel` が生成する（そのウィンドウを開くのが設定ウィンドウのため） |
| D3 | 共有状態の置き場所 | 処理中フラグ・`StatusMessage`・`LastResultPath`・サービス・`AppSettings` は **親**。登録済みモデルの一覧とライブ用の選択は **`SettingsViewModel`**。写しは持たない |
| D4 | 子から親の状態を見る方法 | **読み取り専用の転送プロパティ**（`public bool IsNotBusy => _main.IsNotBusy;`）。親のサービス・設定は `internal` のアクセサー（`AudioCaptureService` / `TranscriptionService` / `SpeakerDiarizationService` / `AppSettings`）で渡す |
| D5 | 変化の中継 | **2 か所に限る。** ① `FileTranscriptionViewModel.SetTranscribing` — `IsTranscribingFile` の唯一の書き手で、子の側の通知もここで出す。② `SettingsViewModel.OnMainPropertyChanged` — 親の `IsNotBusy` の変化を設定ウィンドウへ中継する |
| D6 | 登録済みモデルの一覧の操作と書き戻し | モデル管理の並べ替え・名前の変更は、一覧を差し替えている間にライブ用の選択が外れても保存しないよう、`SettingsViewModel.ChangeWhisperModelsWithoutWriteBack` で包む（分割前は同じクラスの抑止フラグを直接立てていた） |
| D7 | 設定の保存 | `MainViewModel.SaveSettings` が 1 か所で書く。値は子（`Settings.*`・`FileTranscription.SelectedFileLanguage`）から集める |
| D8 | ファイルの割り方 | ADR-0008 規則 7（1 ファイル 500 行が目安）。`FileTranscriptionViewModel` は 595 行になったため、実行部分を `FileTranscriptionViewModel.Run.cs` に割った |
| D9 | テストでの UI 文言の検査 | `UiTextTests` の「画面に出してはいけない開発用の語」の正規表現を、`MainViewModel` から `\w+ViewModel` に広げる（ViewModel の名前が増えたため。検査の意図は同じ） |

## 4. 仕様書への影響

要件は変わらない。**実装箇所の欄と構成の記述だけ** を直した。

- [x] `docs/spec/01_requirements.md` — 実装箇所の欄を移った先のクラスへ。REQ-REC-13 / REQ-TRX-FILE-09 / REQ-LIVEVIEW の注記 / REQ-SETWIN-04 / REQ-MODELWIN-01 の「同じ `MainViewModel` を共有する」を、それぞれの ViewModel を `DataContext` にする記述へ
- [x] `docs/spec/02_architecture.md` — レイヤー図、ViewModel の一覧（6 クラスとファイル）、中継の 2 か所、依存、`Task.Run` の行
- [x] `docs/spec/03_class_diagram.md` — ViewModel を 6 クラスに、ウィンドウのコンストラクターの型、親子の関係、注記
- [x] `docs/spec/04_sequence_diagram.md` — §1 / §3 / §4 / §6 / §6.1 / §8 の参加者

## 5. アーキテクチャへの影響

- ADR: **要** → [ADR-0008](../adr/0008-per-window-viewmodels.md)（承認済み）。ADR-0002 / 0005 / 0006 は状態を「置換済み（→ 0008）」にした（決定の本文は書き換えない）。
- 規範: `CLAUDE.md` の ViewModel の項、[20-architecture-standards.md](../harness/20-architecture-standards.md) §1 / §2 / §5 / §6、
  [00-ways-of-working.md](../harness/00-ways-of-working.md) の「ADR が要る変更」の例を直した。
- 3 層構成と依存方向は変わらない（ViewModel → Services / Models）。ViewModel 層の中に親子の参照が入る。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 子 4 つの生成と公開、`internal` のアクセサー、`LastTranscriptionError`。`SaveSettings` は子から値を集める |
| `AudioCaptureApp/ViewModels/MainViewModel.Windows.cs`（新規） | 補助ウィンドウを開く入口（イベント 4 つ・「設定…」「文字起こし表示」「ファイルから文字起こし」・ドロップ） |
| `AudioCaptureApp/ViewModels/MainViewModel.Transcription.cs` | 言語・モデル・GPU を `SettingsViewModel` へ移し、話者識別の状態と `TranscriptionEnabled` だけを残す |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs` | 保存先を `Settings.OutputFolder` から読む。終了時はファイル文字起こしの中止を子に任せる |
| `AudioCaptureApp/ViewModels/MainViewModel.AutoStart.cs` | ON/OFF を `Settings.AutoStartRecordingEnabled` から読む。`ResetAutoStart` を子に公開 |
| `AudioCaptureApp/ViewModels/MainViewModel.Devices.cs` | 冒頭のコメントのみ |
| `AudioCaptureApp/ViewModels/SettingsViewModel.cs`（新規） | 設定ウィンドウの状態（保存先・モデルの一覧と選択・GPU・言語・自動開始）とモデルの読み込み |
| `AudioCaptureApp/ViewModels/WhisperModelsViewModel.cs`（`MainViewModel.WhisperModels.cs` から） | モデル管理ダイアログの編集状態と操作 |
| `AudioCaptureApp/ViewModels/FileTranscriptionViewModel.cs` / `.Run.cs`（新規） / `.StartTime.cs`（`MainViewModel.FileTranscription*.cs` から） | ダイアログの入力・実行・中止・開始時刻の推定 |
| `AudioCaptureApp/ViewModels/RecordingMetadataViewModel.cs`（`MainViewModel.RecordingMetadata.cs` から） | 録音停止後のメタデータ入力 |
| `AudioCaptureApp/ViewModels/LiveTranscriptViewModel.cs`（`MainViewModel.LiveTranscript.cs` から） | 文字起こし表示の行の蓄積とまとめ書き |
| `AudioCaptureApp/MainWindow.xaml.cs` ほか補助ウィンドウ 5 枚の `.xaml.cs` | コンストラクターが受け取る型を子の ViewModel に |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` / `RecordingMetadataFileTests.cs` / `UiTextTests.cs` | 静的メンバーの呼び先のクラス名（§8） |
| `docs/adr/0008-per-window-viewmodels.md`（新規）、`docs/adr/0002` / `0005` / `0006` / `README.md` | §5 |
| `CLAUDE.md`、`docs/harness/00-ways-of-working.md` / `20-architecture-standards.md` | §5 |
| `docs/spec/01〜04` | §4 |

**`*.xaml` は 1 つも変えていない。**

## 7. 実装手順

### グループ A — 判断
- [x] **A1** 案を 3 つ並べ、利用者が案 A を選んだ（2026-09-29）
- [x] **A2** ADR-0008 を書き、ADR-0002 / 0005 / 0006 と一覧を更新した

### グループ B — 分割（結合の少ない順）
- [x] **B1** `LiveTranscriptViewModel`（親の状態に触れない）
- [x] **B2** `RecordingMetadataViewModel`（書くのは `LastResultPath` / `StatusMessage` だけ）
- [x] **B3** `SettingsViewModel` と `WhisperModelsViewModel`（モデルの一覧と選択を設定側へ）
- [x] **B4** `FileTranscriptionViewModel`（処理中フラグの書き手を 1 つに）
- [x] **B5** `MainViewModel.Windows.cs` に開く入口を集め、コードビハインドの型を変えた
- [x] **B6** テストの呼び先を直した

### グループ C — 文書
- [x] **C1** `CLAUDE.md` と `docs/harness/` を ADR-0008 の規則に合わせた
- [x] **C2** `docs/spec/01〜04` を直した（§4）

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件（「実行結果」）
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし（同上）
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功（CI で確認した。同上）
- [x] **Z4** 仕様書（§4）の更新を読み直し、文書中の `クラス名.メンバー名` がすべて実在することを機械的に確かめた
- [ ] **Z5** 画面の操作確認（§8 の一覧）— **利用者の手元で行う**

## 8. テスト一覧

**追加なし。** 変更したのは静的メンバーの呼び先のクラス名だけで、アサーションは 1 行も変えていない
（差分の `-` 行と `+` 行を、クラス名を伏せて突き合わせると全行が対になる）。唯一の例外は D9 の正規表現。
テストが無変更のまま全件通ることを「静的な判定ロジックを変えていない」ことの根拠にする。

> **テストで守れない範囲:** 親子の配線（ボタンの可否が切り替わるか、設定が保存されるか、ダイアログを開くたびに既定値が入るか）。
> ViewModel のインスタンスを作るテストは無く（サービスが実機のデバイスと Whisper を要るため）、ここは画面で確かめるしかない。

### 手動確認の一覧（Z5）

分割で配線が変わった箇所を上から順に。**いずれも分割前と同じ動きであること** を見る。

- **設定ウィンドウ**
  - [ ] 「設定…」で開き、保存先・ライブ用モデル・GPU・言語・自動開始が前回の値で出る
  - [ ] 保存先を「選択…」で変え、閉じて開き直すと残っている（`settings.json` にも書かれている）
  - [ ] ライブ用モデルを切り替えると「モデル読み込み中...」→「モデル読み込み完了」になる
  - [ ] GPU を切り替えるとモデルが読み直される。GPU が無い環境では OFF に戻る
  - [ ] 録音中は「設定…」が押せない（`IsNotBusy`）
- **モデル管理ダイアログ**（設定ウィンドウから）
  - [ ] 追加・名前の変更・削除・上へ／下へが効き、閉じると設定ウィンドウのドロップダウンに反映される
  - [ ] 並べ替え・名前の変更のあとも、ライブ用の選択が外れない
  - [ ] ライブ用に選択中のモデルを削除すると、選択が外れて「モデル未設定」になる
- **ファイル文字起こし**
  - [ ] 「ファイルから文字起こし」とドラッグ＆ドロップの両方でダイアログが開き、開始時刻・言語・モデル・メタデータの既定値が入る
  - [ ] モデルのドロップダウンに、モデル管理で登録した一覧が出る
  - [ ] 「開始」で進捗が出て、処理中は「開始」が押せず「中止」が押せる
  - [ ] 「中止」が効き、処理中にダイアログを閉じようとすると確認が出る
  - [ ] 完了後、ステータスバーと「保存先を開く」が結果のファイルを指す
  - [ ] 完了・中止のあと、メインウィンドウの録音開始・「設定…」・デバイス更新・「ファイルから文字起こし」が押せる状態に戻る
- **録音と停止後のメタデータ**
  - [ ] 録音 → 停止でメタデータ入力が出る。「OK」で会議名が付いたファイル名に変わり、JSON ができる。「キャンセル」で何も残らない
- **文字起こし表示**
  - [ ] ライブ文字起こし中に行が流れ、新しい録音を始めると前回の行が消える
- **自動開始**
  - [ ] 設定で ON にすると、マイクの音量で録音が始まる。OFF に戻すと始まらない
- **終了**
  - [ ] ファイル文字起こし中にウィンドウを閉じると確認が出て、「はい」で中止してから終わる

## 9. 未解決の質問

なし（方式は ADR-0008 で承認済み。分割の境界は D2〜D8 で確定）。

## 10. 前提

- CommunityToolkit.Mvvm のソースジェネレーターは、子の ViewModel（`partial class`）にも同じく生成する。G1 で確認した。
- テストは静的メンバーだけを使っている（ADR-0008「現状」）。インスタンスの配線はテストに現れない。

---

## 実行結果 (2026-09-29)

### 品質ゲート

このクラウド環境（Linux）に .NET SDK 10.0.112 を入れて G1 / G2 を実行した。
WPF のビルドには `-p:EnableWindowsTargeting=true`（`dotnet format` は環境変数 `EnableWindowsTargeting=true`）が要る。
G3 は WindowsDesktop のランタイムが無いため Linux では実行できず、CI（windows-latest）で確かめた。

- `dotnet build` : 警告 0 件 / エラー 0 件（Debug、Linux。`--no-incremental` で 2 プロジェクトとも再コンパイル）
- `dotnet format`: 差分なし（終了コード 0、Linux）
- `dotnet test`  : **389 件成功 / 0 件失敗 / 0 件スキップ**（CI）

CI は `build-desktop` の run 36624738792（windows-latest、workflow_dispatch、commit `0707bda`）。
同じ実行で G1 は警告 0 件 / エラー 0 件、G2 は成功（差分なし）。**CI は Release 構成**で、ゲートの規定（Debug）とは構成が違う。
テストの件数は分割前（`develop`、T189 の CI）と同じ 389 件。

### 分割後の行数

| ViewModel | ファイル | 行数 |
|---|---|---|
| `MainViewModel` | `MainViewModel.cs` / `.Recording.cs` / `.Devices.cs` / `.Transcription.cs` / `.Windows.cs` / `.AutoStart.cs` | 360 / 337 / 160 / 118 / 116 / 76 = **1,167** |
| `FileTranscriptionViewModel` | `FileTranscriptionViewModel.cs` / `.Run.cs` / `.StartTime.cs` | 330 / 272 / 131 = **733** |
| `SettingsViewModel` | `SettingsViewModel.cs` | **407** |
| `WhisperModelsViewModel` | `WhisperModelsViewModel.cs` | **327** |
| `LiveTranscriptViewModel` | `LiveTranscriptViewModel.cs` | **124** |
| `RecordingMetadataViewModel` | `RecordingMetadataViewModel.cs` | **119** |
| **合計** | 13 ファイル | **2,877** |

分割前は `MainViewModel` 1 クラス・10 ファイル・**2,527 行**（最大 516 行）。分割後の最大は 407 行で、
1 ファイル 500 行の目安と ADR-0008 規則 8 の 1,500 行（1 つの ViewModel の合計）を満たす。
合計が 350 行増えたのは、転送プロパティ・コンストラクター・クラスごとの定型と説明のコメントのため。

### 計画からの逸脱

1. **起動時のモデル読み込みの呼び出し順が変わった（最終状態は同じ）。** 分割前はコンストラクターの冒頭で
   `TranscriptionEnabled` を入れていたため、ライブ文字起こしが ON のときは、モデルのパスが入る前に 1 度
   `TryLoadWhisperModel` が呼ばれて「モデルパス未設定」を出し、後でもう 1 度呼ばれて読み込んでいた。
   分割後は子（`SettingsViewModel`）がパスを入れてから `TranscriptionEnabled` を入れるので、
   1 回目の呼び出しで読み込みが始まり、2 回目は「読み込み中」の判定で何もしない。
   **読み込みは分割前と同じく 1 回だけで、起動後の状態は変わらない。** 起動直後に一瞬だけ出ていた
   「モデルパス未設定」が出なくなる。
2. **`FileTranscriptionViewModel` をさらに `.Run.cs` に割った**（D8）。移した直後は 595 行だったため。

### 引き継ぎ

- **Z5（画面の操作確認）は利用者の手元で行う。** 回帰が見つかったら本タスクの不具合として起票する。
- ライブ文字起こしの 2 段化の T187（字幕帯）は `LiveTranscriptViewModel` に、T188（用語集）は `SettingsViewModel` に入る。
