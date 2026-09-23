# T168 — 録音の開始を自動化する（案 (a) マイク音量）

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

会議が始まったのに録音を忘れるミスを防ぐ。**終了は自動化しない**（止め忘れは致命的でないが、途中で切れるのは致命的）。

## 2. スコープ境界

**やること**
- 案 (a): マイクのレベルが閾値以上の状態が N 秒続いたら `StartRecording` を呼ぶ（REQ-REC-12）
- 設定 4 つ（REQ-CFG-10）。UI は ON/OFF のチェックボックスのみ（REQ-SETWIN-03 ⑥）
- 自動開始した録音であることの表示（「自動録音中」＋ステータスバー）
- 発火させない条件（忙しい・マイク未選択・モーダル中・停止直後のクールダウン）

**やらないこと（重要）**
- **案 (b) Zoom / Teams の検知は作らない** → T171 として起票（手元に Zoom / Teams が無く、オーディオセッション検知を実機で確かめられないため。未検証の発火条件は「現行に不具合を入れない」制約に反する）
- **案 (c) Google カレンダー、案 (d) キーワード検知は採らない**（起票時の既定案どおり）
- **停止の自動化はしない**
- **閾値・継続時間・クールダウンの UI は作らない**（`settings.json` の手編集。REQ-CFG-06 と同じ扱い）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 採る案 | **(a) のみ**。(b) は T171 へ |
| D2 | 判定の場所 | レベルメーターの 50ms `DispatcherTimer`（UI スレッド）に相乗りする。新しいスレッドを作らないため NFR-01 の問題が起きない |
| D3 | 既定値 | 閾値 −30 dB／継続 3.0 秒／クールダウン 10 秒。閾値は「普通の声で話すとメーターが −30 を超える」程度の値であり、環境で変わるので `settings.json` で調整できる。**実機での適合は未検証**（マイク実機での試験はこのセッションでは行えない） |
| D4 | 誤起動対策 | 「連続して閾値以上」で判定し、下回ったら 0 に戻す（咳・キーボード音の 1 発で起動しない） |
| D5 | モーダル中の抑止 | View がダイアログを `ShowDialog` する前後で `MainViewModel.IsModalDialogOpen` を立て下げする（View → ViewModel の向きなので依存方向は守られる）。終了確認の `MessageBox` も同じ扱い |
| D6 | 表示 | 録音状態の文言を「自動録音中」（5 文字。NFR-09 の固定幅に収まる）にし、ステータスバーに 1 行出す。専用の欄は増やさない |
| D7 | 純粋な判定器 | `Services/AutoStartTrigger`（状態は連続時間と最後の停止からの経過だけ）。`Observe(levelDb, tick, canStart)` を 50ms ごとに呼ぶ。テスト対象 |
| D8 | 停止直後 | `NotifyStopped()` でクールダウンを開始する。エラー停止（`OnRecordingError`）でも同じ |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — **REQ-REC-12 新設**、REQ-REC-07（文言）、REQ-CFG-01、**REQ-CFG-10 新設**、REQ-SETWIN-03 ⑥
- [x] `docs/spec/02_architecture.md` — Service 一覧、`MainViewModel.AutoStart.cs`
- [x] `docs/spec/03_class_diagram.md` — `AutoStartTrigger` / `AutoStartOptions`、`AppSettings` と `MainViewModel` の追加メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §7 新設（GPU 切り替えは §8 へ）

## 5. アーキテクチャへの影響

- ADR: **不要**。Service に純粋な判定器を 1 つ足すだけ。スレッドモデルも変えない（既存の UI タイマー上で判定する）。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AutoStartTrigger.cs` | 新規: `AutoStartOptions`（クランプ）と `AutoStartTrigger` |
| `AudioCaptureApp/Models/AppSettings.cs` | 4 キー |
| `AudioCaptureApp/ViewModels/MainViewModel.AutoStart.cs` | 新規 partial: 設定プロパティ、判定の呼び出し、自動開始 |
| `AudioCaptureApp/ViewModels/MainViewModel.Devices.cs` | `UpdateMeters` から判定を呼ぶ |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs` | 文言、停止時の `NotifyStopped`、`IsAutoStartedRecording` のリセット |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 設定の読み書き |
| `AudioCaptureApp/MainWindow.xaml.cs` | `IsModalDialogOpen` の立て下げ |
| `AudioCaptureApp/SettingsWindow.xaml` | チェックボックス |
| `AudioCaptureApp.Tests/AutoStartTriggerTests.cs` | 新規 |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値 |

## 7. 実装手順

- [x] **A1** `AutoStartOptions` / `AutoStartTrigger` とテスト
- [x] **A2** `AppSettings` の 4 キー
- [x] **A3** `MainViewModel.AutoStart.cs`、`UpdateMeters` からの呼び出し、開始・停止の結線
- [x] **A4** View: モーダルの立て下げ、設定ウィンドウのチェックボックス
- [x] **Z1〜Z4** 品質ゲートと仕様の読み直し

## 8. テスト一覧

- **`Observe_SustainedAboveThreshold_FiresOnce`** — 閾値以上が継続時間に達したら 1 度だけ true
- **`Observe_DropBelowThreshold_ResetsSustain`** — 途中で下回ったら連続時間が 0 に戻る
- **`Observe_CannotStart_DoesNotAccumulate`** — `canStart = false` の間は積算しない
- **`Observe_WithinCooldownAfterStop_DoesNotFire`** — 停止直後のクールダウン中は発火しない
- **`Observe_AfterCooldown_FiresAgain`** — クールダウンを過ぎれば再び発火する
- **`AutoStartOptions_ClampsAndFallsBack`** — 範囲外・非有限値の扱い
- **`DefaultValues_AreCorrect`（既存）** — 既定 OFF と 3 つの値

> **テストで守れない範囲:** 実機マイクでの閾値の適合、`StartRecording` との結線、モーダル中の抑止。

## 9. 未解決の質問

1. **閾値 −30 dB の妥当性** — 実機で確かめる。*既定案: −30 dB のまま出し、`settings.json` で調整できることを README に書く。*

## 10. 前提

- `MicPeakLevel` はマイク選択中は録音の有無に関わらず更新される（REQ-DEV-03 / REQ-LVL-04）。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 295 件成功 / 0 件失敗 / 0 件スキップ（286 → 295、+9）
- 計画からの逸脱: README の「音声録音」に自動開始の説明を 1 行足した。**実機マイクでの動作確認は未実施**（このセッションではアプリを対話的に起動できない）。判定器は単体テストで固定した
