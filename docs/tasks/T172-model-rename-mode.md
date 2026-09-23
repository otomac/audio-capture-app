# T172 — モデル管理画面で名前を変更しようとすると「追加」を押してしまい、重複エラーになる

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

削除して登録し直さなくても、選んだモデルの名前をその場で変えられるようにする。

## 2. 再現（2026-09-22、scratchpad `xamlcheck models`。本体の `WhisperModelsWindow` を実 VM で開いて操作）

一覧で要素を選ぶと名前欄・ファイル欄に値が入り、**「追加」と「名前を変更」の両方が有効**になる。
名前欄の直下にある目立つ（アクセント色の）ボタンが「追加」で、「名前を変更」は一覧の下に離れて置かれていた。
選んだ要素のパスが入ったまま「追加」を押すと `同じファイルが別の名前で登録されています: ggml-small` で必ず失敗する。
（「名前を変更」を押せば成功していたが、導線として気づけない配置だった。）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 直し方 | フォームを**「名前の変更」（要素を選択中）か「追加」（未選択）のどちらか一方のモード**にする。主ボタン 1 つ（名前欄の直下）が `名前を変更` / `追加` に切り替わり、選択中は `AddWhisperModelCommand` の `CanExecute` も false。重複エラーになる組み合わせを押せなくする |
| D2 | 追加モードへの戻り方 | 見出しの右の「新しく追加」で選択を外す（欄も空にする）。「参照…」は追加モードでのみ有効（名前の変更ではパスを変えない。REQ-MODELWIN-03） |
| D3 | 見出し | 「追加」／「選択中のモデルの名前を変更」と、いまどちらのモードかを文言で示す |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-MODELWIN-02（モード）、REQ-MODELWIN-03（主ボタンの位置）
- [x] `docs/spec/03_class_diagram.md` — `IsEditingWhisperModel` / `NewWhisperModel`

## 5. 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.WhisperModels.cs` | `IsEditingWhisperModel`、`NewWhisperModelCommand`、`CanAddWhisperModel` / `CanBrowseWhisperModelFile` にモード条件 |
| `AudioCaptureApp/WhisperModelsWindow.xaml` | 主ボタンの切り替え、見出し、「新しく追加」 |

## 6. 実測（修正後、同じ手順）

- 選択直後: `追加` = Collapsed / `名前を変更` = Visible・有効 / `参照…` = 無効 / `AddWhisperModelCommand.CanExecute` = false
- 名前を編集して「名前を変更」: エラーなし、一覧・ライブ用の選択・一覧の選択が新しい名前に追従
- 「新しく追加」: 追加モードに戻り、欄が空、`参照…` が有効

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 320 件成功 / 0 件失敗 / 0 件スキップ（増減なし。モードの判定は ViewModel の状態で単体テスト不可。上の実測で確認）
- 計画からの逸脱: なし
