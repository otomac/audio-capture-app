# T158 — デバイス一覧の「更新」をメインウィンドウのデバイス選択の隣へ戻す

> **状態:** 完了 — 2026-08-28
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

デバイスを差し替えたときに「一覧を取り直す」と「取り直した一覧から選ぶ」が別ウィンドウに
分かれていて導線が切れている。「一覧を更新」をメインウィンドウのデバイス選択の直下へ戻す。

## 2. スコープ境界

**やること**
- `MainWindow.xaml` の入力カードに「一覧を更新」ボタンを追加する
  （マイク／スピーカーの 2 行の直下・区切り線の上、右寄せ）。
- `SettingsWindow.xaml` の「入力デバイスの一覧を取り直す」行を削除する。
- ボタンが 1 行増えた分だけ `MainWindow` の `Height` を広げる。
- 仕様書 REQ-DEV-01 / REQ-SETWIN-01 / REQ-SETWIN-03 を改訂する。

**やらないこと（重要）**
- **`RefreshDevicesCommand` と `CanRefreshDevices` の中身は 1 行も変えない。** 移すのは置き場だけ。
- **`RefreshDevicesInternal` の選択保持ロジックに触らない。**
- **設定ウィンドウの他の項目（保存先・Whisper モデル・言語・GPU・話者識別）に触らない。**
- **T157 と同じブランチだが、`MainViewModel.FileTranscription.cs` へは T158 として手を入れない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | ボタンの置き場所 | **マイク／スピーカーの 2 行の直下・区切り線の上に右寄せ**（利用者が 2026-08-28 に選択）。カード上部に見出し行を新設する案は採らない |
| D2 | ボタンの文言 | **「一覧を更新」**（設定ウィンドウでの文言をそのまま持ち込む。学習し直しをさせない） |
| D3 | ボタンのスタイル | **`Button.Small`**。主操作（録音開始／停止）より小さく見せ、同じカード内で主従を保つ |
| D4 | 設定ウィンドウ側に残すか | **残さない。** 2 か所に同じ導線があると「どちらが効くのか」を考えさせる（REQ-SETWIN-03 に「ここに置かない」と明記した） |
| D5 | ウィンドウ高さ | **374 → 404。** `ResizeMode="CanMinimize"` で固定高のため、増えた 1 行分を手で足す |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-DEV-01（「更新」の置き場をメインウィンドウへ）、
      REQ-SETWIN-01（メインウィンドウに残すものの一覧に「更新」を追加）、
      REQ-SETWIN-03（設定ウィンドウの項目を 5+1 → 4+1 に。「ここに置かない」を明記）
- [ ] `docs/spec/02_architecture.md` — 影響なし（View の枚数も依存方向も変わらない）
- [ ] `docs/spec/03_class_diagram.md` — 影響なし（`RefreshDevices()` は既に載っており増減しない）
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし（起動時の `RefreshDevices()` 呼び出し順は変わらない）

## 5. アーキテクチャへの影響

- ADR: **不要。** View の中でのコントロールの置き場が変わるだけで、3 層構成・依存方向・
  ウィンドウの枚数・`MainViewModel` の共有（ADR-0002）のいずれにも触れない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/MainWindow.xaml` | 入力カードのスピーカー行の直下に「一覧を更新」を右寄せで追加。`Height` を 374 → 404 |
| `AudioCaptureApp/SettingsWindow.xaml` | 「入力デバイスの一覧を取り直す」の `Grid` を削除。保存先フォルダ行の下マージンを整える |
| `docs/spec/01_requirements.md` | §1 / §12 の 3 要件を改訂（済） |

## 7. 実装手順

### グループ A — メインウィンドウ
- [ ] **A1** スピーカー行の `Grid` の直後、`Card.Divider` の前に「一覧を更新」を追加する (`MainWindow.xaml`)
- [ ] **A2** `Window` の `Height` を 404 にする (`MainWindow.xaml`)

### グループ B — 設定ウィンドウ
- [ ] **B1** 「入力デバイスの一覧を取り直す」の `Grid`（`RefreshDevicesCommand` を含む）を削除する (`SettingsWindow.xaml`)
- [ ] **B2** 直前の「保存先フォルダ」の `Grid` から不要になった下マージンを外す (`SettingsWindow.xaml`)

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [ ] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

**追加しない。** 変更は XAML のレイアウトだけで、`MainViewModel` の公開面も挙動も変わらない。
`RefreshDevicesCommand` の挙動を保証する単体テストは元から無く（実機のデバイス列挙に依存するため
`AudioCaptureServiceTests` も列挙結果に踏み込まない）、本タスクでそれを新設するのはスコープ外である。

> **テストで守れない範囲:** ボタンが実際に描画される位置・押せる状態・ウィンドウ高さの
> 足りているかは XAML のレンダリング結果であり、単体テストでは検証できない。
> `dotnet build` が XAML をコンパイルしてバインド先の解決漏れ（`RefreshDevicesCommand` の綴り誤り等）を
> 拾うところまでが機械で守れる範囲で、見た目は実機確認に頼る。

## 9. 未解決の質問

なし（D1〜D5 で確定済み）。

## 10. 前提

- `RefreshDevicesCommand` は `CommunityToolkit.Mvvm` の `[RelayCommand]` が生成しており、
  バインド先の名前は置き場を変えても同じである。
- `SettingsWindow` と `MainWindow` は同じ `MainViewModel` を `DataContext` に共有している
  （ADR-0002）ので、移動先でもバインドがそのまま通る。

---

## 実行結果 (2026-08-28)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 242 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: **D5 のウィンドウ高さを 402 → 404 に訂正した。** `Button.Small` の
  `MinHeight` 22 に上マージン 8 を足した実測が 30px であり、374 + 30 = 404 のため。
  それ以外の逸脱はなし。
- **機械で確認できていない点:** ボタンの描画位置と、404px でカードが収まりきるかは
  実機で目視する必要がある（§8 のとおり）。
