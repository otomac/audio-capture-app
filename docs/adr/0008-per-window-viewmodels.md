# ADR-0008 — ウィンドウごとに ViewModel を持たせ、`MainViewModel` を親にする

> **状態:** 承認済み — 利用者の判断（2026-09-29。T170 の選択肢から「ウィンドウごとの ViewModel」を選択）
> **日付:** 2026-09-29
> **関連タスク:** [T170](../tasks/T170-per-window-viewmodels.md)
> **置き換える ADR:** [ADR-0002](./0002-secondary-windows-share-mainviewmodel.md) の「補助ウィンドウは `MainViewModel` を共有する」
> （規則 2・3 のイベントと生成の規則は引き継ぐ）、[ADR-0005](./0005-mainviewmodel-split.md) の規則 1・3、
> [ADR-0006](./0006-mainviewmodel-split-reevaluation.md) の再評価契機

## 背景

[ADR-0006](./0006-mainviewmodel-split-reevaluation.md) は、`MainViewModel` の分割（案 A）を機能追加と同じブランチで
やると回帰の切り分けができないとして先送りし、**バッチの統合後に T170 で単独で判断する**と期限を切った。
さらに 2026-09 以降、次のことが重なった。

- **行数:** `MainViewModel*.cs` の合計は **2,527 行**（2026-09-29 の `develop`）で、ADR-0006 の再評価契機①（2,500 行）を超えた。
  最大のファイル `MainViewModel.FileTranscription.cs` も 516 行で、1 ファイル 500 行の目安を超えていた。
- **今後の追加:** ライブ文字起こしの 2 段化（[ADR-0007](./0007-two-pass-live-transcription.md)）で、字幕帯・速報の設定・用語集が
  ViewModel に足される（T187 / T188）。
- **ウィンドウの枚数:** 6 枚（メイン・設定・モデル管理・ファイル文字起こし・メタデータ入力・文字起こし表示）。

T170 で案を並べ、利用者が「ウィンドウごとの ViewModel」を選んだ（2026-09-29）。

## 現状（分割前の実測、2026-09-29）

- `MainViewModel` は 1 クラス・10 ファイル。全ウィンドウが同じインスタンスを `DataContext` にしていた（ADR-0002）。
- **複数のウィンドウが同じ状態を見る**ようになっていた（ADR-0005 の時点ではバインド集合が完全に素だった）。
  - 登録済みモデルの一覧: 設定・モデル管理・ファイル文字起こしの 3 枚
  - メタデータ 3 項目: メタデータ入力・ファイル文字起こしの 2 枚（同じプロパティを使い回していた）
  - 話者識別の状態: メイン・設定・ファイル文字起こしの 3 枚
  - 処理中フラグ（`IsNotBusy`）: メイン・設定の 2 枚
- ステータス表示（`StatusMessage`）は 6 つのファイルから、直近の成果物（`LastResultPath`）は 3 つのファイルから書かれていた。
- 他の機能のメンバーを使う箇所（結合）は、ファイル文字起こし 27・モデル管理 10・メタデータ 5・文字起こし表示 ほぼ 0。
- テストはインスタンスを作らず、静的な関数だけを使っている（154 箇所）。

## 選択肢

### 案 A — ウィンドウごとに ViewModel を作り、`MainViewModel` を親にする（採用）

- **内容:** 補助ウィンドウ 5 枚に 1 つずつ ViewModel を作る。`MainViewModel` はメインウィンドウの ViewModel で、
  子を 1 度だけ生成して保持し、プロパティで公開する。共有する状態は親が持ち、子は親への参照を通して読み書きする。
- **利点:** 1 つのクラスが持つ関心が画面の単位に分かれる。ウィンドウを足しても `MainViewModel` が太らない。
  「このプロパティはどの画面のものか」がクラスで分かる。
- **欠点:** 親子の双方向の参照が入る（親が子を生成し、子が親を呼ぶ）。子の変化を親へ、親の変化を子へ伝える配線が要り、
  伝え忘れが回帰になる（ADR-0005 が案 A を退けた理由）。
- **影響範囲:** `ViewModels/` 全体、補助ウィンドウ 5 枚のコードビハインド、テストの参照先、`CLAUDE.md`、
  `20-architecture-standards.md`、`docs/spec/01〜04`。

### 案 B — 結合の少ない部品だけを子クラスに出す

- **内容:** 親の状態に触れない部品（文字起こし表示、メタデータ入力欄、モデル管理の編集状態）だけを子クラスにする。
  `DataContext` は全ウィンドウで `MainViewModel` のまま。
- **利点:** 回帰の危険が小さい。約 300〜450 行が減る。
- **欠点:** 最大の塊（ファイル文字起こし、約 650 行）が親に残る。「どこまでを子にするか」を結合の強さで決める規則になり、
  画面の単位より判断しにくい。利用者の選択ではない。

### 案 C — 何もしない（ADR-0006 の案 D を続け、閾値を引き上げる）

- 先送りは 3 回目になる。T187 / T188 を足すと約 2,750 行に達し、同じ問いがより悪い条件で繰り返される。
- 関心は 12 個のまま 1 クラスに残る。

## 決定

**案 A を採る。** 決め手は利用者の判断（2026-09-29）。そのうえで、ADR-0005 が挙げた欠点（配線の伝え忘れ）を
構造で抑えるため、次の規則を置く。

### この決定に伴う規則

1. **ウィンドウ 1 枚に ViewModel 1 つ。** 名前は `<ウィンドウ名から Window を除いたもの>ViewModel`
   （例外: メインウィンドウは `MainViewModel`、ファイル文字起こしのダイアログは `FileTranscriptionViewModel`）。
   置き場所は `ViewModels/`。
   | ウィンドウ | ViewModel | 生成・保持 |
   |---|---|---|
   | `MainWindow` | `MainViewModel` | `MainWindow` が `new` する |
   | `SettingsWindow` | `SettingsViewModel` | `MainViewModel.Settings` |
   | `WhisperModelsWindow` | `WhisperModelsViewModel` | `SettingsViewModel.WhisperModelsManager` |
   | `FileTranscriptionOptionsWindow` | `FileTranscriptionViewModel` | `MainViewModel.FileTranscription` |
   | `RecordingMetadataWindow` | `RecordingMetadataViewModel` | `MainViewModel.RecordingMetadata` |
   | `LiveTranscriptWindow` | `LiveTranscriptViewModel` | `MainViewModel.LiveTranscript` |
2. **子は親が 1 度だけ生成し、アプリの終了まで保持する。** ウィンドウを開くたびに作り直さない
   （ダイアログを開くたびの既定値は、子の準備メソッドが入れ直す。例: `FileTranscriptionViewModel.Prepare`）。
   子はコンストラクターで親（必要なら祖先）を受け取る。DI コンテナもメッセンジャーも使わない（ADR-0001 を維持）。
3. **共有する状態は 1 か所に置き、写しを持たない。**
   - 処理中フラグ（`IsRecording` / `IsStopping` / `IsTranscribingFile` → `IsNotBusy`）・ステータス表示（`StatusMessage`）・
     直近の成果物（`LastResultPath`）・サービスと設定の実体は **親** が持つ。
   - 登録済みモデルの一覧とライブ用の選択は **`SettingsViewModel`** が持つ。他の子は同じインスタンスを参照する。
   - 子が親の値を画面に出すときは、読み取り専用の転送プロパティ（`public bool IsNotBusy => _main.IsNotBusy;`）にする。
4. **状態の書き手は 1 つに限る。変化の中継は書き手の側で 1 か所に集める。**
   - `IsTranscribingFile` の書き手は `FileTranscriptionViewModel` だけで、書くのは `SetTranscribing` の 1 か所。
     そこで子の側の通知（`IsTranscribingFile` / `CanStartFileTranscription` / 「中止」の可否）もまとめて出す。
   - 親の値の変化を子が受ける必要があるときは、子のコンストラクターで親の `PropertyChanged` を 1 度だけ購読し、
     1 つのハンドラーで中継する（`SettingsViewModel.OnMainPropertyChanged`）。
5. **XAML のバインド名は、子へ移しても変えない。** 転送プロパティで同じ名前を出す。画面の回帰を XAML で起こさないため。
6. **ウィンドウを開く要求は、そのボタンがあるウィンドウの ViewModel がイベントで上げる。** 生成は View
   （コードビハインド）が行い、`Owner` を設定する（ADR-0002 規則 2・3 を引き継ぐ）。
   モーダルな補助ウィンドウの上に出す子ダイアログは、その補助ウィンドウが生成する（ADR-0006 規則 4 を引き継ぐ。
   `SettingsWindow` → `WhisperModelsWindow`）。
7. **1 ファイル 500 行を目安に、機能単位で `partial` に割る**（ADR-0005 規則 2 を各 ViewModel に適用）。
8. **再評価の契機:** ①1 つの ViewModel の全ファイル合計が **1,500 行** を超えたとき、
   ②子から親、または親から子への中継が **3 か所目** になったとき（規則 4 の「1 か所に集める」が保てなくなった兆候）。

## 結果

- **良くなること:**
  - `MainViewModel` は 6 ファイル・約 1,170 行になった（分割前 2,527 行）。関心はメインウィンドウの操作と共有状態に絞られた。
  - 各ウィンドウの状態がクラスで分かれ、ファイル文字起こし（約 730 行）・設定（約 410 行）・モデル管理（約 330 行）などは
    それぞれの ViewModel で完結する。
  - 速報の字幕帯（T187）は `LiveTranscriptViewModel` に、用語集（T188）は `SettingsViewModel` に入る。
- **悪くなること・受け入れるコスト:**
  - 親子の双方向の参照が入った。中継は 2 か所（規則 4）。新しい共有状態を足すたびに、書き手と中継の置き場所を決める必要がある。
  - 転送プロパティとコメントのぶん、ViewModel 全体の行数は 2,527 → 約 2,880 行に増えた。
  - 画面の動作はユニットテストで守れない（従来どおり）。分割の回帰は実機での操作確認で見る（T170 §8）。
- **後戻りのしやすさ:** 中程度。子を親の `partial` に戻すことは機械的にできるが、XAML の `DataContext` と
  コードビハインドの型も戻す必要がある。

## 追随して更新するもの

- [x] `CLAUDE.md` — 「ViewModel は `MainViewModel` 1 クラスに集約」「`ViewModels/` に `MainViewModel` 以外のクラスを置かない」を本 ADR の規則へ
- [x] `docs/harness/20-architecture-standards.md` — §1 の ViewModel の行、§2 の「ViewModel のクラス分割」、§5 の置き場所、§6 の契機
- [x] `docs/harness/00-ways-of-working.md` — ADR が要る変更の例（「`MainViewModel` の分割」）
- [x] `docs/spec/02_architecture.md` / `03_class_diagram.md` / `04_sequence_diagram.md` / `01_requirements.md`（実装箇所の欄）
- [x] `docs/adr/0002` / `0005` / `0006` — 冒頭に本 ADR への前方参照を 1 行足す（決定本文は書き換えない）
- [x] `docs/adr/README.md` — 一覧に追加
