# ADR-0002 — 補助ウィンドウは MainViewModel を共有する View として追加する

> **状態:** 承認済み
> **日付:** 2026-08-20
> **関連タスク:** [T113](../tasks/T113-file-transcription-options-dialog.md) / [T114](../tasks/T114-live-transcript-window.md)

## 背景

T113（ファイル文字起こしのオプション指定モーダルダイアログ）と T114（ライブ文字起こしの表示
サブウィンドウ）で、**アプリのウィンドウが 1 枚から 3 枚になる**。

[ADR-0001](./0001-baseline-architecture.md) は「DI コンテナも Service 抽象も要らない」という
決定の決め手として、はっきり **「ウィンドウ 1 枚・ViewModel 1 個・Service 3 個」という規模** を
挙げている。ウィンドウが増えると、この前提そのものが動く。放置すると、次の担当者が
「ウィンドウごとに ViewModel を作るのが自然では？」と考えて `MainViewModel` を分割し始め、
`CLAUDE.md` の「ViewModel は `MainViewModel.cs` 1 ファイルに集約」と衝突する。

あわせて、**誰がウィンドウを生成するか** も決めておく必要がある。
[20-architecture-standards.md §1](../harness/20-architecture-standards.md#1-レイヤーと依存方向) の
依存方向は View → ViewModel の一方向であり、ViewModel から View を `new` すると逆流する。
既存の `MainViewModel` は `Microsoft.Win32.OpenFileDialog` / `OpenFolderDialog` を直接開いており、
「ダイアログは ViewModel から開くもの」という前例に見えてしまう点も整理しておきたい。

## 現状

- View 層は `MainWindow.xaml(.cs)` と `Controls/LevelMeterControl` のみ
  （[docs/spec/02_architecture.md §2](../spec/02_architecture.md)）。
- `MainWindow` が `MainViewModel` を直接 `new` し、`DataContext` に設定する。
- ViewModel から View への参照は 1 つも無い。上位への通知は **イベント**
  （`RecordingError` / `Error` / `RuntimeInfo` / `MicMuteChangedExternally`）で行う、というのが
  Service → ViewModel で確立しているパターン。
- `MainViewModel` は 767 行。分割の閾値（1,500 行、
  [20-architecture-standards.md §6](../harness/20-architecture-standards.md#6-構造が壊れかけているサイン)）
  までは余裕がある。

## 選択肢

### 案 A — ウィンドウごとに ViewModel を作る

- **内容:** `FileTranscriptionViewModel` / `LiveTranscriptViewModel` を新設し、各ウィンドウの
  `DataContext` にする。`MainViewModel` から必要な状態を渡す。
- **利点:** 各ウィンドウの関心が独立する。ViewModel が肥大化しない。
- **欠点:** 3 つのウィンドウが**同じ状態**（`IsTranscribingFile`・進捗・文字起こし結果）を
  見るため、ViewModel 間で状態を同期する仕組みが要る。実質メッセンジャーか共有モデルの
  導入になり、DI なしという ADR-0001 の決定と噛み合わない。今の状態量に対して重すぎる。
- **影響範囲:** `ViewModels/` に 2 ファイル追加、`MainViewModel` から状態の切り出し、
  `CLAUDE.md` の「1 ファイル集約」方針の変更。

### 案 B — 補助ウィンドウは `MainViewModel` を共有する View として追加する（採用）

- **内容:** 新しいウィンドウは `MainWindow` と**同じ `MainViewModel` インスタンス**を
  `DataContext` に受け取る、状態を持たない View とする。ウィンドウの生成・表示・破棄は
  **View 層（`MainWindow` のコードビハインド）** が行い、ViewModel は「開いてほしい」を
  **イベント**で通知するだけにする（Service → ViewModel と同じ向きの逃がし方）。
- **利点:** 状態同期の仕組みが要らない（同じインスタンスなので常に一致する）。
  依存方向 View → ViewModel が保たれる。ViewModel の増加ゼロ。
- **欠点:** `MainViewModel` にウィンドウ 3 枚分のプロパティが集まり、行数が増える。
  ウィンドウ単位でのユニットテストはできない（元々できていない）。
- **影響範囲:** ルート直下に `.xaml` を 2 つ追加、`MainWindow.xaml.cs` にウィンドウ生成、
  `MainViewModel` にプロパティ・メソッド・イベントを追加。

### 案 C — 何もしない（ウィンドウを増やさない）

T113 / T114 の要求そのものを満たせない。オプション指定を `MainWindow` に埋め込むと、
ファイル文字起こしのときにしか使わない入力欄が常時居座り、画面が混む。
ライブ文字起こしの表示に至っては、`MainWindow` が `ResizeMode="CanMinimize"` で
`SizeToContent="Height"` のため、可変長のテキスト表示を置く場所が無い。却下。

## 決定

**案 B を採用する。** 補助ウィンドウは、`MainViewModel` を共有する状態レスな View として追加する。

決め手：

- **状態の一元性** — 3 つのウィンドウが見るのは同じ 1 つの進行状態（`IsTranscribingFile`・
  進捗・文字起こし行）である。別々の ViewModel に分けると、真っ先に必要になるのが
  「分けたものを同期する仕組み」であり、分けた意味が消える。
- **依存方向を曲げない** — ウィンドウの生成を View 層に置き、ViewModel からはイベントで
  要求だけを上げる。これは Service → ViewModel で既に使っている型どおりのやり方であり、
  新しい概念を持ち込まない。
- **ADR-0001 との整合** — 変わるのは「ウィンドウ 1 枚」という前提だけで、
  「ViewModel 1 個・DI なし・抽象なし」は維持される。ADR-0001 を置換する必要はない。

### この決定に伴う規則

1. **補助ウィンドウは `DataContext` に `MainViewModel` を受け取り、自前の状態を持たない。**
   コードビハインドに持ってよいのは、ウィンドウ自身の生存管理（表示・アクティブ化・クローズ）だけ。
2. **ウィンドウの生成・表示は `MainWindow` のコードビハインドで行う。**
   `MainViewModel` は `System.Windows.Window` の派生型を参照しない。
3. **ViewModel から View への要求はイベントで通知する。**
   （`FileTranscriptionRequested` / `LiveTranscriptRequested`）
4. **補助ウィンドウは `Owner` に `MainWindow` を設定する。** メインウィンドウを閉じたときに
   一緒に閉じるのは WPF の `Owner` の既定動作であり、自前で追随処理を書かない。
5. `Microsoft.Win32` の**共通ファイルダイアログ**（`OpenFileDialog` / `OpenFolderDialog`）は
   従来どおり ViewModel から開いてよい。フレームワークが提供する OS の共通ダイアログであり、
   本アプリの View ではないため、ここでいう「View への参照」に当たらない。

## 結果

- **良くなること:** ウィンドウが増えても状態同期のバグが構造的に発生しない。
  「新しい画面を足すときに何をすればよいか」が上の 5 つに固定される。
- **悪くなること・受け入れるコスト:** `MainViewModel` が太る。T113 / T114 の追加後で
  約 950 行になる見込みで、分割の閾値 1,500 行に近づく。ウィンドウが 4 枚目・5 枚目と
  増えるなら、この ADR を置換して案 A を選び直すことになる。
- **後戻りのしやすさ:** 高い。プロパティの集まりを新しい ViewModel へ移し、
  `DataContext` の設定先を差し替えるだけで案 A へ移行できる。
  イベントによる「開いてほしい」通知はそのまま使える。

## 追随して更新するもの

- [x] `docs/spec/02_architecture.md` — View 層のレイヤー図と責務の記述にウィンドウ 2 枚を追加
- [x] `docs/spec/03_class_diagram.md` — `FileTranscriptionOptionsWindow` / `LiveTranscriptWindow` を追加
- [x] `docs/harness/20-architecture-standards.md` — §1 の View 層の場所、§5「新しいコードをどこに置くか」に
      補助ウィンドウの行を追加
- [ ] `CLAUDE.md` — 「ViewModel は `MainViewModel.cs` 1 ファイルに集約」は変わらないため更新不要
