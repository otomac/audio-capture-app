# T154 — メインウィンドウの UI 改良

> **状態:** 進行中（中断中 — [T155](./T155-viewmodel-split-adr.md) の ADR 承認待ち）— 2026-08-26
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **設計レビュー:** モックアップと合意の記録（利用者へ提示・2026-08-26 承認）

## 1. 目的

メインウィンドウを **「録音するための操作面」** に絞る。一度決めたら触らない設定は新設する
設定ウィンドウへ移し、あわせて WPF 既定の見た目をフラットな自前スタイルへ差し替える。
台帳 T154 の目的①コンパクト化／②モダン化／③常時表示が不要なものをダイアログへ／
④デザインは合意してから実装、のすべてに対応する。

**現状の一番大きな冗長は、「マイク」と「スピーカー」が「入力デバイス」グループと
「音声ミキサー」グループに分かれて 2 回ずつ出ていること。** ここを 1 デバイス＝1 行に束ねるのが
コンパクト化の本体である。

## 2. スコープ境界

**やること**

- メインウィンドウの再構成（カード 2 枚＋ステータスバー、幅 480・高さ固定）
- `SettingsWindow` の新設（モーダル）と、そこへ移す 5 項目
- 見た目の刷新（`ResourceDictionary` による自前スタイル）
- **状態が変わってもレイアウトが動かないようにする**（D10。既存の 2 か所の是正を含む）
- ドラッグ＆ドロップの受け口をウィンドウ全体へ広げる
- 「話者識別」編集不可チェックボックスの削除

**やらないこと（重要）**

- **機能を足さない・削らない。** 動作は現状と同じで、置き場所と見た目だけを変える。
  唯一の例外が D9（話者識別チェックボックスの削除）であり、これは仕様書を先に直す。
- **NuGet パッケージを追加しない。** UI ライブラリ（ModernWpf 等）は導入しない（D11）。
- **ダークテーマ対応をしない**（D12）。
- **新しい ViewModel を作らない。** `SettingsWindow` は `MainViewModel` を共有する
  状態レス View とする（[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md)）。
- **Service 層に触らない。** `AudioCaptureService` / `TranscriptionService` /
  `SpeakerDiarizationService` は変更しない。
- **`LiveTranscriptWindow` / `FileTranscriptionOptionsWindow` の構成を変えない。**
  共通スタイルが当たることによる見た目の変化は許容し、崩れていないかを目視で確認する。
- **`settings.json` のスキーマを変えない。**

## 3. 決定事項

いずれも 2026-08-26 にモックアップを提示して利用者の合意を得たもの。実装中に蒸し返さない。

| # | 決定 | 結論 |
|---|---|---|
| D1 | 構成 | **案 A（操作面と設定を分ける）。** メインはカード 2 枚（入力＋録音 / 文字起こし）＋ステータスバー。案 B（タブ）は設定タブを開くとメーターと経過時間が見えなくなるため却下。案 C（見た目だけ）は目的①③を満たさないため却下 |
| D2 | 寸法 | **幅 480px・高さ固定。** `SizeToContent="Height"` をやめる。`ResizeMode="CanMinimize"` は維持 |
| D3 | 入力デバイス | **1 デバイス＝1 行。** レベルメーターはコンボボックスの**直下**に細い帯として敷き、ミュートは同じ行の右端。「入力デバイス」と「音声ミキサー」の 2 グループを 1 枚のカードに統合する |
| D4 | レベルメーター | 高さを **16 → 4** に下げ、両端を丸める。**緑／黄／赤の 3 ゾーンは維持する。**単色グラデーションにするとクリップ警告（-3dB 黄・0dB 赤）の情報が落ちるため |
| D5 | ファイル文字起こし | **ボタンとドロップ領域を 1 部品に合体する。** クリック＝ファイル選択、ドロップ＝即開始。`Button` に `ControlTemplate`（破線枠）と `AllowDrop` を与える |
| D6 | D&D の受け口 | **ウィンドウ全体。** 合体でボタンが小さくなるぶんの補償。オーバーレイは従来どおり重ね置きで、ウィンドウいっぱいに出す |
| D7 | 設定ウィンドウ | **新設・モーダル**（`ShowDialog`）。導線はステータスバーの「設定…」。ADR-0002 のとおり `MainViewModel` を `DataContext` として共有する状態レス View とし、生成・表示は `MainWindow` のコードビハインドが行い、ViewModel は**イベントで開いてほしいことだけを通知**する |
| D8 | 設定へ移すもの | **保存先フォルダ / デバイス一覧の更新 / Whisper モデル / ライブ言語 / GPU 使用**の 5 つ。メインに残すのは「入力デバイスの選択」と「ライブ文字起こしの ON/OFF」（利用者が録音のたびに触ると回答した 2 つ） |
| D9 | 話者識別チェックボックス | **削除する。** 同じ内容がステータスバーにも出ており（T152 で両方入れた）、編集もできない表示だったため。状態表示はステータスバー 1 か所に寄せる。これに伴い**唯一の利用者が居なくなる `IsSpeakerDiarizationReady` と `IsSpeakerDiarizationReadyFor` も削除**する（`SpeakerDiarizationStatus` と `SpeakerDiarizationTooltip` は残す） |
| D10 | レイアウトの安定 | **状態で変わってよいのは色と文字だけで、寸法と位置は変えない。**「ミュート」→「ミュート中」でボタンが広がって隣のメーターが縮む、録音ステータスが 3→5 文字になって経過時間が横にずれる、といった意味のない動きを作らない |
| D11 | ライブラリ | **NuGet の追加なし。** `ResourceDictionary` で自前のスタイルを持つ（`CLAUDE.md`「ライブラリ追加は個別承認制」） |
| D12 | ダークテーマ | **対象外。** `ControlTemplate` が倍になるうえ目的①〜③と関係がない。やるなら別タスク |
| D13 | 「設定…」の有効／無効 | **録音中・停止処理中・ファイル文字起こし中は無効化する**（REQ-SETWIN-05）。設定ウィンドウの項目はいずれも REQ-REC-09 でその間は操作できず、開いても何もできない。一方でモーダル（D7）であるため、開いている間は**録音の停止操作が塞がれる**。得るものが無く塞ぐものがある以上、開かせない |

### D10 の内訳（どこで何を固定するか）

| 部品 | 状態で変わる中身 | 固定のしかた |
|---|---|---|
| ミュートトグル | `ミュート` / `ミュート中` | **最長ラベルに合わせた固定幅**（`MinWidth` ではなく `Width`）＋中央寄せ。背景色だけが変わる |
| 録音ステータス | `停止中` / `録音中` / `停止処理中` | 経過時間との**横並びをやめ、上の行**へ移し、右寄せの固定幅の枠に入れる |
| 経過時間 | `00:00:00` 〜 | 等幅フォント。桁数は常に 8 |
| 録音開始／停止 | 有効・無効のみ（文言は不変） | 現状どおり固定幅。無効時は色だけ落とす |
| デバイスのコンボ | デバイス名（長さがまちまち） | 幅は Grid の `*` で決め、はみ出しは省略記号 |
| ステータスバーのメッセージ | 可変長 | 残り幅いっぱい＋`TextTrimming="CharacterEllipsis"`（現状どおり） |
| 話者識別の表示 | `利用可` / `利用不可` / `モデル未配置` | 固定幅。最長に合わせる |
| ドラッグ中のオーバーレイ | 表示・非表示 | レイアウトに影響しない重ね置き（現状どおり） |

> **現状には実際に 2 か所ある。**
> ① `MainWindow.xaml` のミュートトグルは `MinWidth="60"` ＋ `Padding="8,0"` なので
> `ミュート`（3 文字）→ `ミュート中`（4 文字）でボタンが広がり、**隣のレベルメーターが縮む**。
> ② `RecordingStatusText` は `停止中` / `録音中` / `停止処理中`
> （`MainViewModel.cs:559`）で、これが経過時間と横並びの中央寄せなので、
> **停止するたびに時計が左右にずれる**。どちらも本タスクで潰す。

## 4. 仕様書への影響

「仕様書先行」により、実装（グループ C 以降）に入る前にすべて反映する。

- [x] `01_requirements.md` §1 REQ-DEV-01 — 「更新」の導線を設定ウィンドウへ。**デバイスの選択はメインに残す**ことを明記
- [x] `01_requirements.md` §2 REQ-REC-07 — 録音状態の文言と経過時間を横に並べない（NFR-09）
- [x] `01_requirements.md` §5 **REQ-LVL-07 を新設** — 3 ゾーン（緑／黄／赤）の色分けと、寸法を変えても保つこと（D4）。コンボボックス直下に置くこと（D3）
- [x] `01_requirements.md` §8 REQ-TRX-LIVE-14 — ライブ言語のドロップダウンが「メインウィンドウ」→「設定ウィンドウ」
- [x] `01_requirements.md` §9 REQ-TRX-FILE-01 / 02 / 04 — 選択とドロップが 1 部品に合体（D5）、D&D の受け口が「文字起こしグループ」→「ウィンドウ全体」（D6）
- [x] `01_requirements.md` §10 REQ-GPU-02 — GPU チェックボックスの置き場が設定ウィンドウになる
- [x] `01_requirements.md` §12 REQ-LIVEVIEW-01 — 「表示」ボタンの置き場が文字起こしカードのヘッダーになる
- [x] `01_requirements.md` §13 REQ-TRX-DIA-15 — **メインウィンドウの編集不可チェックボックスを削除**し、状態表示はステータスバー 1 か所とする（D9）
- [x] `01_requirements.md` **§14「設定ウィンドウ」を新設**（REQ-SETWIN-01〜06） — 何をメインに残し何を移すか、モーダルであること（D7）、`IsNotBusy` で無効化すること（D13）、保存の契機（REQ-CFG-05 のまま）
- [x] `01_requirements.md` **NFR-09 を新設** — 状態の変化で UI 部品の寸法・位置を変えない（D10）
- [x] `02_architecture.md` — View 一覧・レイヤー図・責務の記述に `SettingsWindow` と `Styles/` を追加
- [x] `03_class_diagram.md` — `SettingsRequested` / `ShowSettings` 追加、`IsSpeakerDiarizationReady` / `IsSpeakerDiarizationReadyFor` 削除を反映

> **REQ-SETWIN-04（`MainViewModel` を `DataContext` として共有する）だけは
> [T155](./T155-viewmodel-split-adr.md) の結論待ちである。** 他の要件は結論に依存しない。

## 5. アーキテクチャへの影響

**ADR: 必要。T155 の結論を待つ（本タスクはそこで中断中）。**

当初「ADR-0002 に 4 枚目として乗るだけなので不要」と書いたが、**誤りだった。**
ADR-0002 は「結果」の節で、自ら再評価の契機を指定している。

> `MainViewModel` が太る。実測で 767 行 → **788 行**（T113 / T114 の追加後）。
> 分割の閾値 1,500 行にはまだ距離があるが、**ウィンドウが 4 枚目・5 枚目と増えるなら、
> この ADR を置換して案 A を選び直すことになる。**

`SettingsWindow` は **4 枚目**にあたる。あわせて実測すると、

| 項目 | ADR-0002 記述時 | 2026-08-26 現在 |
|---|---|---|
| `MainViewModel.cs` の行数 | 788 | **1,396** |
| 分割の閾値（[20-architecture-standards.md §6](../harness/20-architecture-standards.md)） | 1,500 | 1,500 |
| ウィンドウ枚数 | 3 | 3（T154 で 4） |

T154 が増やすのは概算 +10 行（`SettingsRequested` / `ShowSettings` / `CanShowSettings` の追加と
`IsSpeakerDiarizationReady` 系の削除の差引）で約 1,406 行。閾値は超えないが**残りは約 100 行**である。

したがって **ViewModel 分割の是非を先に決める**（利用者の判断・2026-08-26）。
起票は **[T155](./T155-viewmodel-split-adr.md)**。T154 は T155 の ADR が承認されるまで中断する。

- 3 層構成と依存方向そのものは、どちらの結論でも不変。View → ViewModel の一方向を保つため、
  `SettingsWindow` の生成は `MainWindow.xaml.cs` が行い、ViewModel からはイベントで通知する。
- **T155 の結論によって変わるのは「どの ViewModel を `DataContext` にするか」だけ**であり、
  §3 の D1〜D12（画面の見た目とレイアウト）には影響しない。したがって §4 の仕様書修正のうち
  **REQ-SETWIN-04（`MainViewModel` を共有する）だけが結論待ち**で、他は先に確定してよい。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Styles/Theme.xaml`（新規） | 色・寸法・タイポグラフィのリソース定義 |
| `AudioCaptureApp/Styles/Controls.xaml`（新規） | `Button` / `ToggleButton` / `ComboBox` / `TextBox` / `CheckBox` / カード（旧 GroupBox）／ドロップボタンの `Style` と `ControlTemplate` |
| `AudioCaptureApp/App.xaml` | 上の 2 つを `MergedDictionaries` に追加 |
| `AudioCaptureApp/MainWindow.xaml` | 全面的な書き換え（カード 2 枚＋ステータスバー、幅 480・高さ固定）。埋め込みの GroupBox テンプレートは `Styles/` へ移す |
| `AudioCaptureApp/MainWindow.xaml.cs` | D&D のハンドラーをグループからウィンドウへ移す。`SettingsWindow` の生成・表示を追加 |
| `AudioCaptureApp/SettingsWindow.xaml`（新規） | 設定ウィンドウ。`DataContext` は `MainViewModel`（ADR-0002） |
| `AudioCaptureApp/SettingsWindow.xaml.cs`（新規） | コンストラクターで `DataContext` を受け取るだけの状態レス View |
| `AudioCaptureApp/Controls/LevelMeterControl.xaml` | 高さ 16 → 4、角丸。3 ゾーンの色は維持（D4） |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `SettingsRequested` イベントと `ShowSettingsCommand` を追加。`IsSpeakerDiarizationReady` と `IsSpeakerDiarizationReadyFor` を削除（D9） |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `IsSpeakerDiarizationReadyFor_OnlyAvailable_IsTrue` を削除。`ShowSettings` がイベントを発火することのテストを追加 |
| `docs/spec/01_requirements.md` | §4 のとおり |
| `docs/spec/02_architecture.md` | View 一覧に `SettingsWindow` / `Styles/` |
| `docs/spec/03_class_diagram.md` | ViewModel の増減を反映 |
| `docs/adr/0002-secondary-windows-share-mainviewmodel.md` | 関連タスクに T154 を追記 |
| `docs/tasks/backlog.md` | 状態の更新 |

## 7. 実装手順

### グループ A — 仕様書（実装より先）
- [x] **A1** §1 / §2 / §5 / §8 / §9 / §10 / §12 / §13 を D1〜D13 に合わせて更新し、
      **§14「設定ウィンドウ」（REQ-SETWIN-01〜06）と NFR-09（レイアウトの安定）を新設** (`01_requirements.md`)
- [x] **A2** View 一覧へ `SettingsWindow` と `Styles/` を追加 (`02_architecture.md`)
- [x] **A3** `MainViewModel` の増減を反映 (`03_class_diagram.md`)
- [ ] **A4** ADR の扱い → **[T155](./T155-viewmodel-split-adr.md) へ分離。** 結論が出たら
      REQ-SETWIN-04 と本タスク §5 を確定させる

> **ここで中断中。** グループ B 以降は T155 の ADR が承認されてから着手する。

### グループ B — スタイル基盤
- [ ] **B1** `Styles/Theme.xaml` を作る（色・寸法・書体）
- [ ] **B2** `Styles/Controls.xaml` を作る（各コントロールの `Style` / `ControlTemplate`）
- [ ] **B3** `App.xaml` から両者をマージする
- [ ] **B4** この時点で既存 3 ウィンドウが崩れていないことを目視確認する

### グループ C — ViewModel
- [ ] **C1** `SettingsRequested` イベントと `ShowSettingsCommand` を追加
- [ ] **C2** `IsSpeakerDiarizationReady` / `IsSpeakerDiarizationReadyFor` を削除（D9）

### グループ D — 設定ウィンドウ
- [ ] **D1** `SettingsWindow.xaml(.cs)` を新規作成（保存先 / 一覧更新 / モデル / ライブ言語 / GPU / 話者識別の状態）
- [ ] **D2** `MainWindow.xaml.cs` で `SettingsRequested` を購読し `ShowDialog` で開く

### グループ E — メインウィンドウ
- [ ] **E1** カード 2 枚＋ステータスバーへ再構成し、幅 480・高さ固定にする
- [ ] **E2** 入力デバイスを 1 デバイス＝1 行にまとめる（D3）
- [ ] **E3** 録音ステータスを経過時間の上の行へ移し、固定幅にする（D10）
- [ ] **E4** ミュートトグルを固定幅にする（D10）
- [ ] **E5** ファイル文字起こしのボタンとドロップ領域を合体させる（D5）
- [ ] **E6** D&D のハンドラーをウィンドウへ移し、オーバーレイをウィンドウいっぱいに出す（D6）
- [ ] **E7** 話者識別チェックボックスを削除する（D9）

### グループ F — コントロール
- [ ] **F1** `LevelMeterControl.xaml` を高さ 4・角丸にする。3 ゾーンは維持（D4）

### グループ Z — 検証
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [ ] **Z4** 起動して手動確認（§8 の「テストで守れない範囲」）
- [ ] **Z5** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

本タスクの変更はほぼ XAML であり、ユニットテストで守れる範囲は狭い。

- **`ShowSettings_RaisesSettingsRequested`** — `ShowSettingsCommand` を実行すると
  `SettingsRequested` が 1 回発火する（`LiveTranscriptRequested` の既存テストと同じ形）
- **削除**: `IsSpeakerDiarizationReadyFor_OnlyAvailable_IsTrue` — 対象プロパティを消すため（D9）

> **テストで守れない範囲（Z4 で目視確認する）:**
> ① 幅 480 で最長のデバイス名・最長のステータス文言がはみ出さないこと。
> **日本語の字幅はフォント設定で変わりうるため、実際に最長の状態を出して確かめる。**
> ② ミュートの ON/OFF と録音の開始/停止でレイアウトが動かないこと（D10 の本体）。
> ③ ウィンドウのどこへドロップしても文字起こしが始まること、オーバーレイが出ること。
> ④ 設定ウィンドウがモーダルで開き、閉じると設定が効いていること。
> ⑤ `LiveTranscriptWindow` と `FileTranscriptionOptionsWindow` が共通スタイルで崩れていないこと。
> ⑥ レベルメーターが高さ 4 でも黄・赤ゾーンが判別できること。

## 9. 未解決の質問

**画面については無い**（D1〜D13 で確定。すべて 2026-08-26 に利用者の合意を得ている）。

**残る 1 件は ViewModel の構造** — `SettingsWindow` の `DataContext` を `MainViewModel` にするか、
専用の ViewModel にするか。[T155](./T155-viewmodel-split-adr.md) の ADR で決める。
本タスクはそこで中断している。

## 10. 前提

- T149 / T151 / T152 / T153 はいずれも完了済みで、`develop` に入っている。台帳 T154 が言う
  「先に片付けてから着手するほうが差分が小さくなる」条件は満たしている。
- 話者ダイアライゼーションの有効化は `settings.json` のままで、UI から切り替えられない
  （REQ-TRX-DIA-03）。D9 で削除するのは**表示専用のチェックボックス**であり、機能ではない。
- ライブ言語の変更は次の録音開始から効く（REQ-TRX-LIVE-14）。設定ウィンドウへ移しても
  この性質は変わらないため、その旨をウィンドウ上に 1 行で書く。

---

## 実行結果

（未実施）
