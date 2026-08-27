# T114 — 文字起こしテキストを表示するサブウィンドウ

> **状態:** 完了 — 2026-08-20
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

文字起こしされたテキストを、録音中にその場で読めるようにする。現在はテキストが
`.txt` に書かれるだけで、**アプリの画面には一切出ていない**（`SegmentTranscribed` イベントを
誰も購読していない）。録音を止めてファイルを開くまで結果が分からない。

## 2. スコープ境界

**やること**
- 文字起こし行を時刻順に表示するサブウィンドウを追加する（初期 320x240・リサイズ可・9pt）。
- メインウィンドウから開く導線を付ける。
- 録音停止では閉じない。プロセス終了（メインウィンドウを閉じる）で閉じる。
- `TranscriptionService.SegmentTranscribed` を `MainViewModel` が購読する（現在は未購読）。

**やらないこと（重要）**
- **`.txt` への書き出しは一切変えない。** 表示は追加であって、出力の置き換えではない。
- **テキストの編集・検索・保存機能は付けない。** 表示のみ。
- **録音開始時に表示行を自動で消さない。** 「録音停止で閉じない」という要求は、
  前のセッションの内容を残して見続けたいという意図と読める。消す判断は利用者に委ねる
  （そもそも消す手段を今回は付けない。§8 の積み残し参照）。
- **ウィンドウを自動で開かない。** 録音開始に連動して開くとは要求されていない。
- **新しい ViewModel を作らない**（[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md)）。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ライブ文字起こしだけを表示するか、ファイル文字起こしの行も表示するか | **両方表示する。** `SegmentTranscribed` はライブ／ファイル共通のイベントであり、行の先頭にラベル（`[マイク]` / `[スピーカー]` / `[ファイル]`）が入るので区別は付く。ファイル文字起こし中に進み具合が読めるのは実用上ありがたい |
| D2 | 「プロセス終了で閉じる」の実現方法 | **`Owner` に `MainWindow` を設定する。** WPF は所有ウィンドウを閉じると被所有ウィンドウを閉じる。自前で追随処理を書かない（ADR-0002 の規則 4） |
| D3 | 「録音停止で閉じない」の実現方法 | **何もしない。** ウィンドウの生存は録音状態と無関係に作ってあるため、明示的に閉じる処理を書かなければこの要求は満たされる。「書かないこと」が実装なので、テストではなくこのタスク票で意図を残す |
| D4 | 9pt の指定 | **XAML に `FontSize="9pt"` と書く。** WPF の `FontSize` は既定で DIP（1/96 インチ）だが単位付き指定ができる。`12` と書くより意図が読める |
| D5 | 表示行数の上限 | **1,000 行。超えたら古い行から捨てる。** 長時間録音で `ObservableCollection` が無制限に伸びるのを防ぐ。捨てられるのは表示だけで、`.txt` には全行が残る |
| D6 | 何度も開いたときの扱い | **ウィンドウは 1 つだけ。** 既に開いていれば `Activate()` で手前に出す。生存管理は `MainWindow` のコードビハインドが持つ |
| D7 | 導線の置き場所 | **文字起こし設定 GroupBox のヘッダー。** ライブ文字起こしのチェックボックスの隣。録音中・処理中でも押せる（見るための窓なので無効化しない） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — §12「文字起こし表示ウィンドウ」を新設（REQ-LIVEVIEW-01 〜 06）
- [x] `docs/spec/02_architecture.md` — View 層に `LiveTranscriptWindow`（T113 の commit で反映済み）
- [x] `docs/spec/03_class_diagram.md` — `LiveTranscriptWindow` と `MainViewModel` の新メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §3 の `SegmentTranscribed` の行き先を明示

## 5. アーキテクチャへの影響

**あり**（T113 と同じ理由）。[ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md) が
両方をカバーしているため、追加の ADR は起票しない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/LiveTranscriptWindow.xaml` | 新規。行の一覧表示（9pt・リサイズ可） |
| `AudioCaptureApp/LiveTranscriptWindow.xaml.cs` | 新規。最新行への自動スクロール |
| `AudioCaptureApp/MainWindow.xaml` | ヘッダーに「文字起こし表示」ボタン |
| `AudioCaptureApp/MainWindow.xaml.cs` | `LiveTranscriptRequested` を購読。ウィンドウの生存管理 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `SegmentTranscribed` の購読、`LiveTranscriptLines`、要求イベント、コマンド |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 行の追加と上限のテスト |

## 7. テスト一覧

- **`AppendLiveTranscriptLine_AddsToEnd`** — 行が時刻順（追加順）に積まれる
- **`AppendLiveTranscriptLine_OverLimit_DropsOldest`** — 上限を超えたら**先頭**から捨てる
  （末尾から捨てると最新行が消えるという致命的な取り違えを検出する）
- **`AppendLiveTranscriptLine_AtLimit_KeepsAll`** — ちょうど上限なら捨てない（境界）

> **テストで守れない範囲:** ウィンドウの初期サイズ・リサイズ可否・9pt・自動スクロール・
> `Owner` によるプロセス終了時の連動クローズ・「録音停止で閉じない」は、いずれも WPF の
> ウィンドウを必要とするためユニットテストで検証できない。
> `SegmentTranscribed` から `Dispatcher` 経由で行が届く経路も、Whisper と WPF の
> ディスパッチャーが要るため未検証。固定できたのは行バッファの操作だけである。

## 8. 未解決の質問

離席中のため既定案で確定した。戻ったら確認してほしい。

1. **表示をクリアする手段が無い。** 録音を何度も回すと前のセッションの行が上に残り続ける
   （1,000 行を超えた分は自然に落ちる）。「クリア」ボタンを付けるか、録音開始時に自動で
   消すかは要求に無かったため付けていない。*既定案: 現状のまま（付けない）。*
2. **ラベルによる絞り込み（マイクだけ / スピーカーだけ）は付けていない。** 要求に無いため。
   会議で相手の声だけ追いたい場面はありそうなので、必要なら起票する。

## 9. 前提

- `SegmentTranscribed` は文字起こしワーカースレッドから発火する。よって `Dispatcher` を
  経由しないとバインド対象のコレクションを触れない（NFR-01 / 20-architecture-standards §3-1）。
- `ObservableCollection<string>` への UI スレッドからの追加は、`ListBox` の仮想化と併用しても
  安全である。

---

## 実行結果 (2026-08-20)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 121 件成功 / 0 件失敗 / 0 件スキップ
- 起動確認: `AudioCaptureApp.exe` を起動 → 稼働継続 → 正常終了
- 計画からの逸脱: なし

## 10. 積み残し・気づいたこと

- **`SegmentTranscribed` の購読は今回が初めて。** これまで誰も購読していなかったため、
  イベントが「非 UI スレッドから発火する」ことに依存する利用者がいなかった。
  今後この経路に処理を足すときは、必ず `Dispatcher.BeginInvoke` の内側に置くこと。
- **ウィンドウを開いたままの状態は保存していない。** 次回起動時は閉じた状態で始まる。
  `settings.json` に持たせるかは要求に無いので触っていない。
- **`MainViewModel` は 788 行（T113 前は 767 行）。** ADR-0002 は肥大化を懸念していたが、
  実測では 2 つのウィンドウを足して 21 行の増加に留まった。分割の閾値は 1,500 行
  （[20-architecture-standards.md §6](../harness/20-architecture-standards.md#6-構造が壊れかけているサイン)）で、
  まだ十分な余裕がある。ADR-0002 の「悪くなること」の見積もりは実測へ書き直した。
