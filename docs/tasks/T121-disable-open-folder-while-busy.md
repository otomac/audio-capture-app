# T121 — 録音中は「保存先を開く」を無効化する

> **状態:** 完了 — 2026-08-18
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [T111](./T111-open-result-folder.md)（本タスクはその追補）

## 1. 目的

一度録音を終えて「保存先を開く」が有効になった後、続けて次の録音を開始しても
ボタンが押せたままになっている。このとき開くのは **1 つ前の** 成果物のフォルダであり、
進行中の録音の成果物ではない。表示としても紛らわしいため、作業中は無効化する。

## 2. スコープ境界

**やること**

- 録音中・録音停止処理中・ファイル文字起こし中は `OpenResultFolderCommand` を無効にする

**やらないこと（重要）**

- **`LastResultPath` の破棄。** 作業中は「開けない」だけにし、値は保持する。作業が終われば
  （新しい成果物が出なくても）直前の成果物を再び開ける状態に戻る
- **ボタンの位置・文言・開き方の変更。** T111 の D4／D6 の決定はそのまま
- **他のボタンの活性条件の変更。** 既存の `IsNotBusy` 利用箇所には触らない

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 無効化する範囲 | **`IsNotBusy`（録音中・停止処理中・ファイル文字起こし中）。** 依頼は「録音中」だが、停止処理中はまだ成果物が確定しておらず、ファイル文字起こし中も保持しているのは前回の成果物で、違和感の理由は同じ。既存の `CanToggleGpu`（REQ-GPU-04）と同じ条件に揃える |
| D2 | 値を消すか、押せなくするか | **押せなくする。** `LastResultPath` を録音開始時にクリアすると、録音が失敗・無音で終わったときに直前の成果物へ辿り着く手段まで失われる |
| D3 | テストの担保範囲 | **判定を静的メソッドに切り出して単体テストする。** `MainViewModel` は WASAPI デバイスを掴むためテストから生成できず、既存テストも純粋関数のみを対象にしている |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — 「11. 成果物フォルダを開く」に **REQ-OPEN-05** を追加
- [-] `docs/spec/03_class_diagram.md` — 公開メンバーの増減なし（`CanOpenResultFolder` は private、
      追加する `CanOpenResultFolderFor` は `internal` のテスト用ヘルパー）
- [-] `docs/spec/02_architecture.md` — 層構成・依存方向は不変
- [-] `docs/spec/04_sequence_diagram.md` — 処理順序は不変

## 5. アーキテクチャへの影響

なし。ViewModel 内の `CanExecute` 条件の変更のみ。

- ADR: **不要**

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `CanOpenResultFolderFor` を追加し `CanOpenResultFolder` から使う。`_isRecording` / `_isStopping` / `_isTranscribingFile` に `[NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]` を追加 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `CanOpenResultFolderFor` のテストを追加 |
| `docs/spec/01_requirements.md` | §4 のとおり |

## 7. 実装手順

- [x] **A1** `CanOpenResultFolderFor(string, bool)` を追加し、`CanOpenResultFolder` を委譲に変える
- [x] **A2** 3 つの busy フラグから `OpenResultFolderCommand` へ再評価を通知する
- [x] **B1** テストを追加
- [x] **Z1** `dotnet build` — 警告 0 件
- [x] **Z2** `dotnet format --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test` — 全件成功
- [x] **Z4** 仕様書の更新反映を読み直す

## 8. テスト一覧

- **`CanOpenResultFolder_IdleWithResult_IsTrue`** — 待機中で成果物ありなら有効
- **`CanOpenResultFolder_Busy_IsFalse`** — 成果物があっても作業中なら無効（本タスクの本体）
- **`CanOpenResultFolder_IdleWithoutResult_IsFalse`** — 起動直後は無効（REQ-OPEN-04 の維持）

> **テストで守れない範囲:** `IsNotBusy` の変化がボタンの活性へ実際に伝わることは
> `[NotifyCanExecuteChangedFor]` のソースジェネレータ任せで、単体テストでは検証できない（手動確認）。

## 9. 未解決の質問

なし（§3 の決定で判断した）。

## 10. 前提

- `IsNotBusy` は既に `!IsRecording && !IsStopping && !IsTranscribingFile` として存在し、
  デバイス選択・ライブ文字起こし ON/OFF・GPU 切り替えの活性条件に使われている。

---

## 実行結果 (2026-08-18)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **61** 件成功 / **0** 件失敗 / **0** 件スキップ
  （既存 58 件 ＋ 新規 3 件）
- 計画からの逸脱: なし

### 手動確認をお願いしたいこと

`[NotifyCanExecuteChangedFor]` によるボタン活性の追従は単体テストで検証できないため、以下は手動確認が必要。

1. 録音 → 停止 → 「保存先を開く」が**有効**になる
2. そのまま 2 回目の録音を開始 → ボタンが**無効**になる（本タスクの本体）
3. 2 回目の録音を停止 → 停止処理中は**無効**、完了後に**有効**へ戻り、開くのは**新しい**成果物
4. 音声ファイルからの文字起こし中も**無効**、完了後に**有効**へ戻る
