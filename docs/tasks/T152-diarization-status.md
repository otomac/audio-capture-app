# T152 — 話者識別が使える状態かをメインウィンドウで示す

> **状態:** 完了 — 2026-08-24
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

話者ダイアライゼーションは `settings.json` でしか有効化できず（REQ-TRX-DIA-03）、
**画面からは有効かどうかも、モデルが置いてあるかも分からない。** 起動時に判定して
メインウィンドウに出し、「話者欄が出るはずなのに出ない」を実行前に気付けるようにする。

## 2. スコープ境界

**やること**
- 起動時に 3 状態（**有効／モデル未配置／無効**）を判定する。
- ステータスバーに常時表示する。
- 「音声ファイルから文字起こし」ボタンの隣に**編集不可のチェックボックス**「話者識別」を置く。

**やらないこと（重要）**
- **UI から話者識別を切り替えられるようにしない。** 有効化は `settings.json` のまま
  （REQ-TRX-DIA-03）。チェックボックスは**状態表示専用**で操作できない。
- **起動時にモデルを読み込まない。** 遅延読み込み（ADR-0003 N2 / REQ-TRX-DIA-08）は維持する。
  したがって「壊れたモデル」は起動時には検出できない（D2）。
- **判定を実行時に更新しない。** 起動後に `settings.json` やモデルを置き換えても表示は変わらない（D4）。
- **`SpeakerDiarizationService` の生成条件・`EnsureLoaded` の中身を変えない。**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 判定の方法 | **台帳の既定案 (a)。モデル 2 ファイルの存在検査だけを起動時に行う。** 読み込みはしない |
| D2 | (a) の限界 | **「壊れたモデル」「読み込みに失敗するモデル」は検出できない。** 起動時に読むと ADR-0003 N2（モデル未配置でも起動を妨げない）の判断を覆すことになり、ADR が要る。表示の文言も「使えるはず」であって保証ではない旨が分かる語にする |
| D3 | 3 状態の区別 | **有効／モデル未配置／無効。** 「無効」は `SpeakerDiarizationEnabled = false`、「モデル未配置」は有効だがファイルが揃っていない場合 |
| D4 | 更新の契機 | **起動時に 1 度だけ。** 判定は `MainViewModel` のコンストラクターで確定し、以後変えない。設定もモデルパスも UI から変更できないため、途中で変わる契機が無い |
| D5 | ステータスバーの出し方 | **`StatusMessage` を上書きせず、専用の欄を右側に追加する。** `StatusMessage` は起動直後に Whisper ランタイム情報などで上書きされるため、そこに書くと消える |
| D6 | チェックボックスの「編集不可」の作り | **`IsEnabled="False"`（灰色表示）＋ `ToolTipService.ShowOnDisabled="True"`。** 押せないことが見て分かる形にし、詳細はツールチップで補う |
| D7 | 判定ロジックの置き場 | ファイルの存在検査は **`SpeakerDiarizationService.ModelFilesExist`**（Service 層）、3 状態の決定と文言は **`MainViewModel` の `internal static` 純粋関数** |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — §13 に **REQ-TRX-DIA-15**（状態表示）を新設
- [x] `docs/spec/03_class_diagram.md` — `SpeakerDiarizationService.ModelFilesExist`、
      `MainViewModel` の状態プロパティと純粋関数を追記
- [ ] `docs/spec/02_architecture.md` — 影響なし
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし

## 5. アーキテクチャへの影響

- ADR: **不要。** ADR-0003 N2（遅延読み込み）を**維持する**ための設計であり、覆していない。
  存在検査は読み込みではない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/SpeakerDiarizationService.cs` | `ModelFilesExist`（`internal static`）を追加 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `DiarizationAvailability` / 判定と文言の純粋関数 / 表示用プロパティを追加 |
| `AudioCaptureApp/MainWindow.xaml` | チェックボックスとステータスバーの欄を追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 3 状態の判定と文言のテスト |
| `AudioCaptureApp.Tests/SpeakerDiarizationServiceTests.cs` | 存在検査のテスト（新規） |
| `docs/spec/01_requirements.md` | REQ-TRX-DIA-15 を追加 |
| `docs/spec/03_class_diagram.md` | 追加分を反映 |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** §13 に REQ-TRX-DIA-15 を追加 (`01_requirements.md`)
- [x] **A2** クラス図へ反映 (`03_class_diagram.md`)

### グループ B — Service
- [x] **B1** `ModelFilesExist` を追加 (`SpeakerDiarizationService.cs`)

### グループ C — ViewModel
- [x] **C1** `DiarizationAvailability` と判定・文言の純粋関数を追加
- [x] **C2** コンストラクターで判定し、表示用プロパティへ入れる

### グループ D — View
- [x] **D1** 「話者識別」チェックボックスを追加 (`MainWindow.xaml`)
- [x] **D2** ステータスバーに状態欄を追加 (`MainWindow.xaml`)

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`DiarizationAvailabilityFor_Disabled_IsDisabled`** — 設定が無効なら、モデルがあっても「無効」
- **`DiarizationAvailabilityFor_EnabledWithoutModels_IsModelMissing`** — 有効だがファイルが無ければ「モデル未配置」
- **`DiarizationAvailabilityFor_EnabledWithModels_IsAvailable`** — 両方揃っていれば「有効」
- **`DiarizationStatusTextFor_AllStates_AreDistinctAndNonEmpty`** — 3 状態が区別できる文言になっている
- **`IsSpeakerDiarizationReadyFor_OnlyAvailable_IsTrue`** — チェックが入るのは「有効」のときだけ
- **`DiarizationTooltipFor_AllStates_AreDistinctAndNonEmpty`** — ツールチップも 3 状態で異なる
- **`ModelFilesExist_BothPresent_IsTrue`** — 実ファイル 2 つが揃っていれば真
- **`ModelFilesExist_OneMissing_IsFalse`** — 片方でも欠ければ偽
- **`ModelFilesExist_BlankPath_IsFalse`** — パス未設定は偽（`File.Exists("")` に頼らない）

> **テストで守れない範囲:** ステータスバーとチェックボックスの見た目、
> ツールチップが無効時にも出ること（`ShowOnDisabled`）は手動確認。
> **「モデルはあるが壊れている」場合に「有効」と表示されること**は D2 の既知の限界であり、
> テストで守る対象ではない（そう表示されるのが仕様である）。

## 9. 未解決の質問

なし（D1〜D7 で確定。既定案 (a) は利用者の指示 2026-08-23）。

## 10. 前提

- `SpeakerDiarizationEnabled` / モデルパスは UI から変更できない（REQ-TRX-DIA-03）。
  したがって起動時 1 回の判定で足りる。
- モデル 2 ファイルが存在すれば、通常は読み込みも成功する（壊れている場合は実行時に
  REQ-TRX-DIA-11 のエラーになる）。

---

## 実行結果 (2026-08-24)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 205 件成功 / 0 件失敗 / 0 件スキップ（うち T152 追加分 10 件）
- 計画からの逸脱: ツールチップ用のプロパティ `SpeakerDiarizationTooltip` と
  `DiarizationTooltipFor` を追加した（D6 の「詳細はツールチップで補う」を実装するために必要だったが、
  §6 の変更ファイル一覧を書いた時点では明示していなかった）
- 手動確認が要る範囲: チェックボックスとステータスバーの見た目、
  無効なコントロールでツールチップが出ること（`ShowOnDisabled`）

### develop 取り込み後の再実行 (2026-08-25)

T149 (PR#35) / T150 (PR#36) / T151 (PR#37) のマージを取り込み、競合（台帳・`MainWindow.xaml`・
テストの 3 ファイル）を解消したうえで再実行した。

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 227 件成功 / 0 件失敗 / 0 件スキップ（develop の 217 件 ＋ T152 の 10 件）

**`MainWindow.xaml` の競合は機械的に解けない形だった。** 本タスクは「話者識別」チェックボックスを
「文字起こしにGPUを使用する」の隣へ足しており、その並びには当時「中止」ボタンがあった。
一方 T151 は同じ `StackPanel` から**その「中止」ボタンを削除している**（進捗表示をダイアログへ
集約したため）。本タスク側の差分をそのまま採ると **T151 の削除が巻き戻る**ので、
チェックボックスだけを残し「中止」ボタンは復活させなかった。
解消後、`MainWindow.xaml` に `CancelFileTranscriptionCommand` と `ProgressBar` が
1 件も無いことを確認している。
