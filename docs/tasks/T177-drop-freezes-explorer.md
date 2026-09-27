# T177 — ドラッグ＆ドロップで文字起こしを始めるとドラッグ元のエクスプローラーが固まる

> **状態:** 完了 — 2026-09-27（完了 — 実機確認は未実施）
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

音声ファイルをドロップして文字起こしダイアログを開いている間も、ドラッグ元のエクスプローラーを操作できるようにする。

## 2. スコープ境界

**やること**
- `MainWindow.Window_Drop` で `TranscribeDroppedFile` を `Dispatcher.BeginInvoke` で後回しにする

**やらないこと（重要）**
- **ダイアログのモーダル性は変えない**（REQ-TRX-FILE-09）
- **ファイル選択ダイアログの経路（REQ-TRX-FILE-01）は触らない**（ドラッグ元が無いので起きない）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 原因 | OLE のドラッグ＆ドロップでは、ドロップ先の `Drop` ハンドラーが戻るまでドラッグ元の `DoDragDrop` が戻らない。ハンドラーの中で `ShowDialog` していたため、ダイアログを閉じるまでエクスプローラーが待たされていた |
| D2 | 直し方 | **`Dispatcher.BeginInvoke`（既定優先度）で後回しにする。** 後回しにする範囲は ViewModel の呼び出しだけで、`e.Handled` とオーバーレイの非表示はハンドラー内で済ませる。View 層の変更に閉じ、ViewModel は変えない |
| D3 | 後回しにしている間に状態が変わったら | `TranscribeDroppedFile` の先頭で `CanTranscribeFromFile` を見直しているので、その間に録音が始まっていれば何もしない（既存の挙動） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-02 に「Drop ハンドラーの中でダイアログを開かない」を追記
- [x] `docs/spec/04_sequence_diagram.md` — §6 のドロップ経路に `Dispatcher.BeginInvoke` を追記

## 5. アーキテクチャへの影響

- ADR: 不要（View のコードビハインド内の呼び出し順だけ。層・依存方向・スレッドモデルは変えない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/MainWindow.xaml.cs` | `Window_Drop` で `Dispatcher.BeginInvoke(() => _viewModel.TranscribeDroppedFile(filePath))` |

## 7. テスト一覧

> **テストで守れない範囲:** OLE ドラッグ＆ドロップとドラッグ元プロセスの挙動。実機で確認する。

## 8. 実機確認の手順（未実施）

1. エクスプローラーから `.mp3` をメインウィンドウへドロップする
2. オプション指定ダイアログが開いた状態で、ドラッグ元のエクスプローラーでフォルダを移動・スクロールできること

## 実行結果 (2026-09-27)

作業環境（Linux コンテナ）に .NET SDK が無いため、PR [#49](https://github.com/otomac/audio-capture-app/pull/49) の CI（`build-desktop` / windows-latest、**Release 構成**）で実行した。

- dotnet build : 警告 0 / エラー 0
- dotnet format: 差分なし
- dotnet test  : 389 件成功 / 0 件失敗 / 0 件スキップ
- 実機確認: 未実施
