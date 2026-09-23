# T173 — モデル管理画面で登録済みモデルの並び順を入れ替えられるようにする

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

よく使うモデルをドロップダウンの上に置けるようにする。

## 2. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 操作 | 「上へ」「下へ」で選択中の要素を 1 つずつ動かす（ドラッグ＆ドロップは作らない。実装量に対して得るものが小さい） |
| D2 | 保存 | 動かした時点で保存（REQ-CFG-05）。`WhisperModelList` の順序がそのままドロップダウンの順序 |
| D3 | 選択の保持 | `ObservableCollection.Move` は同じインスタンスを動かすだけだが、`Move` の通知で ComboBox が選択を触ることがあるため、書き戻し抑止のうえでライブ用の選択と一覧の選択を復元する |
| D4 | 端 | 先頭では「上へ」、末尾では「下へ」を無効にする |

## 3. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — **REQ-MODELWIN-08 新設**
- [x] `docs/spec/03_class_diagram.md` — `MoveWhisperModelUp` / `MoveWhisperModelDown`

## 4. 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.WhisperModels.cs` | `MoveWhisperModelUpCommand` / `MoveWhisperModelDownCommand` / `MoveManagedWhisperModel` |
| `AudioCaptureApp/WhisperModelsWindow.xaml` | 「上へ」「下へ」（「削除」の左） |

## 5. 実測（2026-09-22、scratchpad `xamlcheck models`）

- 2 件（small-renamed, medium）で #1 を選択: 上へ=有効 / 下へ=無効
- 上へ: 順序 `medium, small-renamed`、ライブ用の選択は `small-renamed` のまま、一覧の選択は動いた要素（index 0）に追従、上へ=無効 / 下へ=有効
- 下へ: 元の順序に戻り、`settings.json` の `WhisperModelList` も同じ順序で保存されていた

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 320 件成功 / 0 件失敗 / 0 件スキップ（増減なし。並び替えは ViewModel の状態で単体テスト不可。上の実測で確認）
- 計画からの逸脱: なし
