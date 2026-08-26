# T154 — メインウィンドウの UI 改良

> **状態:** 完了 — 2026-08-27
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

> **REQ-SETWIN-04 は [ADR-0005](../adr/0005-mainviewmodel-split.md) の承認（2026-08-27）により確定した。**
> 書き直しは不要で、§4 の仕様書修正はこれで全項目そろっている。

## 5. アーキテクチャへの影響

**ADR は [ADR-0005](../adr/0005-mainviewmodel-split.md) として起票し、2026-08-27 に承認された。**
本タスクへの影響は **無し**（`SettingsWindow` は当初の設計どおりでよい）。

当初このタスク票には「ADR 不要」と書いていたが、**誤りだった。**
ADR-0002 は「結果」の節で、ウィンドウが 4 枚目に増えるならこの ADR を置換して案 A を
選び直す、と自ら再評価の契機を指定している。`SettingsWindow` はその 4 枚目にあたる。
そこで [T155](./T155-viewmodel-split-adr.md) を起こして再評価し、本タスクは中断した。

**結論（ADR-0005 案 D）:** `MainViewModel` は **1 クラスのまま**とし、ファイルだけを
機能単位に `partial` で割る。ウィンドウごとの ViewModel（案 A）は採らない。
決め手は、各ウィンドウのバインド集合が**完全に素**（重複 0）である一方、
`IsNotBusy` / `StatusMessage` / `LastResultPath` / `LiveTranscriptLines` は
**書き手が画面をまたいでいる**ため、分割すると現在 1 本も無い「子 → 親の通知配線」が
必要になることだった。

したがって **REQ-SETWIN-04 は書いたとおりで確定**である —
`SettingsWindow` は `MainViewModel` を `DataContext` として共有する状態レス View とし、
生成・表示は `MainWindow` のコードビハインドが行い、ViewModel はイベントで通知する
（ADR-0002 の規則 1〜5 はすべて有効）。

分割そのものは [T156](./T156-mainviewmodel-partial-split.md) で実施済み（2026-08-27 完了）。
**本タスクが `MainViewModel` へ足す `SettingsRequested` / `ShowSettings` / `CanShowSettings` は
`MainViewModel.cs`（共有状態と導線を持つファイル）へ置く。**
`IsSpeakerDiarizationReady` / `IsSpeakerDiarizationReadyFor` の削除（D9）は
`MainViewModel.Transcription.cs` が対象になる。

3 層構成と依存方向は不変。

> **注意（T156 からの引き継ぎ）:** `partial` 化で定型行が 6 セットに増えたため、
> `MainViewModel` の全ファイル合計は **1,443 行**（ADR-0005 の再評価契機① 1,500 行まで 57 行）。
> 本タスクの概算 +10 行で約 1,453 行になる。**閾値は超えないが、数え方の見直しは別途要判断**
> であり、本タスクでは触らない。

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
- [x] **A4** ADR の扱い → **[T155](./T155-viewmodel-split-adr.md) へ分離し、[ADR-0005](../adr/0005-mainviewmodel-split.md) として承認された**（2026-08-27）。
      REQ-SETWIN-04 と本タスク §5 を確定させた。分割の実施は [T156](./T156-mainviewmodel-partial-split.md)（完了済み）

> **2026-08-27 に再開。** グループ B から着手する。

### グループ B — スタイル基盤
- [x] **B1** `Styles/Theme.xaml` を作った（色・角丸・書体・**固定寸法**）
- [x] **B2** `Styles/Controls.xaml` を作った（Window / TextBlock / Button / ToggleButton / TextBox / CheckBox / ComboBox / ProgressBar ＋ カード・ステータスバー・ピル・ドロップ部品）
- [x] **B3** `App.xaml` から `Controls.xaml` をマージした（`Controls.xaml` が `Theme.xaml` を取り込む）
- [~] **B4** 既存ウィンドウの目視確認 → **利用者に依頼中**（`FileTranscriptionOptionsWindow` と `LiveTranscriptWindow` は暗黙スタイルの影響を受けるため）

### グループ C — ViewModel
- [x] **C1** `SettingsRequested` / `ShowSettingsCommand` / `CanShowSettings`（`IsNotBusy`）を追加
- [x] **C2** `IsSpeakerDiarizationReady` / `IsSpeakerDiarizationReadyFor` を削除（D9）

### グループ D — 設定ウィンドウ
- [x] **D1** `SettingsWindow.xaml(.cs)` を新規作成（保存先 / 一覧更新 / モデル / ライブ言語 / GPU / 話者識別の状態）
- [x] **D2** `MainWindow.xaml.cs` で `SettingsRequested` を購読し `ShowDialog` で開く

### グループ E — メインウィンドウ
- [x] **E1** カード 2 枚＋ステータスバーへ再構成。**幅 480 × 高さ 374 に固定**（`SizeToContent` を外し、実測値を入れた）
- [x] **E2** 1 デバイス＝1 行。メーターはコンボ直下、ミュートは同じ行の右端
- [x] **E3** 録音ステータスを経過時間の上の行へ移し、幅 72 に固定（D10）
- [x] **E4** ミュートトグルを幅 78 に固定（`MinWidth` をやめた。D10）
- [x] **E5** ボタンとドロップ領域を 1 部品に合体（破線は `Rectangle` の `StrokeDashArray`。`Border` では引けないため）
- [x] **E6** D&D を `Window` の `AllowDrop` へ移し、オーバーレイを全行にまたがる重ね置きにした（D6）
- [x] **E7** 話者識別チェックボックスを削除。状態はステータスバーのピル 1 か所（D9）

### グループ F — コントロール
- [x] **F1** `LevelMeterControl.xaml` を高さ 16 → 4 に。**3 ゾーンの色分けは維持**し、未点灯部分の色だけ新パレットへ（D4）

### グループ Z — 検証
- [x] **Z1** `dotnet build` — 警告 0 件 / エラー 0 件
- [x] **Z2** `dotnet format --verify-no-changes` — 差分なし（終了コード 0）
- [x] **Z3** `dotnet test` — 240 件成功 / 0 件失敗（241 件から D9 で 1 件削除）
- [~] **Z4** 起動・正常終了と**全バインドの解決**は自動で確認した。**見た目と対話操作は利用者に依頼中**
- [x] **Z5** 仕様書（§4）の更新反映を読み直した

## 8. テスト一覧

**追加なし。削除 1 件。**

- **削除**: `IsSpeakerDiarizationReadyFor_OnlyAvailable_IsTrue` — D9 で
  `IsSpeakerDiarizationReadyFor` ごと消えるため。241 件 → **240 件**。

**当初この欄に書いた `ShowSettings_RaisesSettingsRequested` は書けなかった。**
このテストは `MainViewModel` のインスタンスを必要とするが、コンストラクターは
`RefreshDevicesInternal()` で WASAPI のデバイス列挙を行い、Whisper モデルの読み込みも試みる。
[20-architecture-standards.md §7](../harness/20-architecture-standards.md) は
**「オーディオデバイス・Whisper モデルの実体を要求するテストは書かない」**と定めており、
既存 54 件のテストも 1 つとして `MainViewModel` を `new` していない。
`ShowSettings` は `SettingsRequested?.Invoke()` の 1 行、`CanShowSettings` は `IsNotBusy` そのもので、
純粋関数として切り出す価値のあるロジックも無い。**したがって本タスクは
ユニットテストで守れる範囲を持たない。** 検証は Z1〜Z4 と手動確認で行う。

> **テストで守れない範囲（手動で確認する）:**
> ① 幅 480 で最長のデバイス名・最長のステータス文言がはみ出さないこと。
> **日本語の字幅はフォント設定と DPI で変わりうるため、実機で最長の状態を出して確かめる。**
> **高さは 374 に固定した**ので、環境によっては下端が切れうる点も併せて見る。
> ② ミュートの ON/OFF と録音の開始/停止でレイアウトが動かないこと（D10 の本体）。
> ③ ウィンドウのどこへドロップしても文字起こしが始まること、オーバーレイが出ること。
> ④ 設定ウィンドウがモーダルで開き、閉じると設定が効いていること。録音中は「設定…」が押せないこと。
> ⑤ `LiveTranscriptWindow` と `FileTranscriptionOptionsWindow` が共通スタイルで崩れていないこと。
> ⑥ レベルメーターが高さ 4 でも黄・赤ゾーンが判別できること。

## 9. 未解決の質問

**画面については無い**（D1〜D13 で確定。すべて 2026-08-26 に利用者の合意を得ている）。

**ViewModel の構造も決着した** — [ADR-0005](../adr/0005-mainviewmodel-split.md)（承認済み・2026-08-27）により、
`SettingsWindow` の `DataContext` は `MainViewModel` を共有する。専用 ViewModel は作らない。

## 10. 前提

- T149 / T151 / T152 / T153 はいずれも完了済みで、`develop` に入っている。台帳 T154 が言う
  「先に片付けてから着手するほうが差分が小さくなる」条件は満たしている。
- 話者ダイアライゼーションの有効化は `settings.json` のままで、UI から切り替えられない
  （REQ-TRX-DIA-03）。D9 で削除するのは**表示専用のチェックボックス**であり、機能ではない。
- ライブ言語の変更は次の録音開始から効く（REQ-TRX-LIVE-14）。設定ウィンドウへ移しても
  この性質は変わらないため、その旨をウィンドウ上に 1 行で書く。

---

## 実行結果 (2026-08-27)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : **240 件成功 / 0 件失敗 / 0 件スキップ**（241 件から D9 で 1 件削除）
- 起動確認: ウィンドウが出て `exit=0` で閉じる。**リソースディクショナリの解決も
  これで確認できている**（`pack://` の指定を誤ると起動時に例外になるため）
- バインド検査: `MainWindow` / `SettingsWindow` / 既存 2 ウィンドウの `{Binding ...}` を
  全て `MainViewModel` のメンバーと突き合わせ、**未解決 0 件**。
  WPF のバインド誤りは無言で失敗するため、機械的に照合した

### 寸法

`SizeToContent="Height"` のまま一度起動して実測し（`GetWindowRect` で 480 × 374、DPI 100%）、
その値を `Height="374"` として固定した（D2）。現状は 520 × 可変。

### 計画からの逸脱

1. **`Button.DropTarget` の破線を `Rectangle` で描いた。** `Border` は `StrokeDashArray` を
   持たないため、`ControlTemplate` の外枠を `Rectangle`（`RadiusX/Y=6`）にした。
2. **`StoppedBrush` を純黒 `#000000` から `#26303F` に変えた。** 新しいパレットの
   「本文には純黒を使わない」に合わせたもので、`MainViewModel.Recording.cs` の 1 行。
   スコープ境界の「機能を足さない・削らない」には触れないが、ViewModel を触った点は記録しておく。
3. **§8 のテストが書けなかった**（上記）。起票時の見積もりが誤っていた。
4. **改行コードで G2 が 2 回落ちた。** 新規ファイルを LF ＋末尾改行ありで作ってしまい、
   `.editorconfig` の `end_of_line = crlf` / `insert_final_newline = false` に反した。
   CRLF ＋末尾改行なしへ揃えて解消。

### 利用者レビュー 1 回目の指摘と対応 (2026-08-27)

| # | 指摘 | 対応 |
|---|---|---|
| 1 | 赤い「録音開始」ボタンの文字が黒くて見づらい | **原因は暗黙の `TextBlock` スタイルだった。** `ContentPresenter` が文字列コンテンツから作る `TextBlock` にも暗黙スタイルが当たるため、そこで `Foreground` を設定すると `Button` 側の白文字を上書きしてしまう。暗黙スタイルから `Foreground` を外し、文字色は `Window` から継承させるようにした（同じ理由で指摘 5 も直った） |
| 2 | サブウィンドウのデザインをメインに合わせる | `FileTranscriptionOptionsWindow` と `LiveTranscriptWindow` から**地色の直書き**（`#FFE0E0E0`）と**ローカルの `Button` スタイル**を外し、共通スタイルに任せた。ダイアログはカード構成へ組み直し、直書きの色（`#FF555555` / `#FF1E88E5` / `#FFCC0000`）を `Text.Label` / `Text.Hint` / `Text.Error` に置き換えた。`ListBox` / `ListBoxItem` のスタイルも追加 |
| 3 | 「（推定値です。不要なら消してください）」が不要 | `StartTimeHintFor` の 3 文言から削除。**REQ-TRX-FILE-15 が求めるのは「どれを使ったか」の表示**であり、そこは残っているので仕様の変更にはあたらない |
| 4 | 「中止」ボタンが赤である必要はない | `Background="#FFCC0000"` / `Foreground="White"` の直書きを外し、通常配色にした |
| 5 | 設定ウィンドウの青いボタンの文字が黒い | 指摘 1 と同一原因のため同時に解消 |

あわせて、指摘 2 の組み直しで使われなくなった `BusyPanel` スタイルを削除した。

**再実行:** build 警告 0 / format 差分なし / test 240 件成功 0 件失敗。バインド未解決 0 件。

### 利用者レビュー 2 回目の指摘と対応 (2026-08-27)

| # | 指摘 | 対応 |
|---|---|---|
| 6 | 言語の選択に `TranscriptionLanguage { Code = ja, ... }` と出る | **自前の `ComboBox` テンプレートが `DisplayMemberPath` を反映していなかった。** デバイス側は `AudioDevice.ToString()` が `FriendlyName` を返すため偶然まともに見えており、気付けていなかった。**4 か所すべて**（メインのマイク／スピーカー、設定のライブ言語、ダイアログのファイル言語）を `ItemTemplate` の明示へ変え、`DisplayMemberPath` はコードベースから無くした |

> **教訓:** 既定のテンプレートを自前に差し替えると、`DisplayMemberPath` のように
> **テンプレート側が読み取る前提のプロパティが黙って効かなくなる。**
> `AudioDevice` が `ToString()` を上書きしていたせいで症状が片方だけ隠れており、
> 起動確認とバインド検査のどちらでも検出できなかった。

### B4 / Z4（目視確認）の結果

利用者が 2 回のレビューで確認し、指摘 1〜6 をすべて解消した。
**ただし指摘 6 の修正（`ItemTemplate` 化）は、修正後の表示を誰も目で見ていない。**
コンボボックス 4 か所の表示は次に起動したときに確認すること。

### 発見した別件

**話者識別の実行中に「中止」を押しても、押せたことが利用者に伝わらない。**
止まらないこと自体は REQ-TRX-DIA-12 のとおりの仕様（キャンセルは推論の開始前と完了後でしか
評価しない）であり、本タスクでは直さない。**[T157](./backlog.md) として起票した** —
「中止」を押した直後にボタンを無効化し、「中止を要求しました（話者識別完了までお待ちください）」
を表示する。中止の判定ロジックそのものは変えない。
