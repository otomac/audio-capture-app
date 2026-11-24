# T214 — 録音の自動開始の判定を実時間で数える

> **状態:** 完了 — 2026-11-24
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

録音の自動開始（REQ-REC-12）の判定器には、レベルメーターのタイマーが 1 回回るごとに名目の 50ms を足していた。
`DispatcherTimer` は 50ms より遅れて回るため、累積 1 秒・落ち込み許容 0.3 秒・クールダウン 10 秒がどれも実際より長くなる。
T210（無音の警告）では同じ数え方で 10 秒の設定が 12.8 秒かかり、実時間で測るよう直した。自動開始にも同じ問題が残っている。

## 2. スコープ

- 変えるもの: 判定器に渡す経過時間（名目 50ms → 前回の更新からの実時間）。1 回に足す上限 0.1 秒
- **変えないもの:** 判定器（`AutoStartTrigger`）の数え方、既定値（−15 dB / 1.0 秒 / 0.3 秒 / 10 秒）、タイマーの間隔（50ms）、無音の警告の上限（1 秒）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 実時間に直すか、名目値のまま仕様に書くか | **実時間に直す**（利用者の判断 2026-11-24）。設定の秒数と仕様の実測値（50ms 窓の実時間）がアプリの動きと揃う |
| D2 | 時間の測り方 | レベルメーターの更新（`UpdateMeters`）で 1 度だけ実時間を測り、自動開始と無音の警告の両方に渡す。上限は判定ごとに決める（`ClampMeterStep`） |
| D3 | 1 回に足す上限 | **0.1 秒**（名目の 2 回分）。UI スレッドが止まっていた間のレベルは最後の 1 回分しか見ていない。無音の警告と同じ 1 秒にすると、止まった直後の 1 回の大きな音だけで累積 1 秒に届いてしまう。普段の間隔（約 62.5ms）には効かない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-REC-12（時間は実時間で測る。1 回 0.1 秒まで）

## 5. アーキテクチャへの影響

- ADR: 不要（ViewModel の中で判定器に渡す値を変えるだけ。層・スレッドは変わらない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.Devices.cs` | `_meterClock` で実時間を測る。`ClampMeterStep` |
| `AudioCaptureApp/ViewModels/MainViewModel.AutoStart.cs` | 名目の `MeterTick` を廃止し、実時間を受け取る。`MaxAutoStartObserveStep`（0.1 秒） |
| `AudioCaptureApp/ViewModels/MainViewModel.SilenceWarning.cs` | 自前の Stopwatch をやめ、`UpdateMeters` が測った実時間を受け取る（振る舞いは T210 のまま） |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | テスト 5 件 |

## 7. テスト一覧

- **`ClampMeterStep_WithinMax_ReturnsElapsedAsIs`** — 実際の間隔（62.5ms）を 50ms に丸めない
- **`ClampMeterStep_LongerThanMax_ReturnsMax`** — 止まっていた 2.4 秒は、自動開始で 0.1 秒・無音の警告で 1 秒に抑える
- **`ClampMeterStep_Negative_ReturnsZero`**
- **`AutoStartStep_AfterUiStall_OneLoudTickDoesNotFillSustain`** — 止まった直後の 1 回の大きな音では発火しない
- **`AutoStartStep_RealTimerInterval_FiresAfterSustainInRealTime`** — 62.5ms 間隔なら 16 回（1.0 秒）で発火する（名目で数えると 20 回 = 1.25 秒）

## 8. 確認（修正前後を同じ音声で比べた）

`C:\personal\dev\audio-capture-work\t214-autostartcheck\`。本体の判定器（`AutoStartTrigger`）と `ClampMeterStep` をそのまま使い、
音声（送信許可済みの Youtube テスト、先頭 10 分）の 10ms ごとのピーク（マイクのモニタ値と同じ取り方）を判定器に渡す。
「理想」は音声時間で正確に 50ms ごとに取った場合で、仕様の実測値（REQ-REC-12）と同じ数え方である。

| 数え方 | タイマーの間隔 | 初回の発火 | 10 分の成立回数 |
|---|---|---|---|
| 理想（50ms ちょうど） | 50ms | 3.9 秒 | 141 回 |
| 修正前（名目 50ms） | 62.5ms（模擬） | 4.1 秒 | 106 回 |
| **修正後（実時間）** | 62.5ms（模擬） | **3.9 秒** | **123 回** |
| 修正前（名目 50ms） | 70ms（模擬） | 7.2 秒 | 96 回 |
| **修正後（実時間）** | 70ms（模擬） | **4.0 秒** | **119 回** |
| 修正前（名目 50ms） | 本物の `DispatcherTimer`（平均 62.6ms） | 4.3 秒 | 112 回 |
| **修正後（実時間）** | 本物の `DispatcherTimer`（同じ回） | 5.7 秒 | **119 回** |

- 修正後は、タイマーの間隔が変わっても理想に近いままである。修正前は、間隔が延びるほど理想から離れる（70ms で初回 7.2 秒）。
- 本物のタイマーでは、この回の修正後の初回が 5.7 秒と遅く出た。初回の時刻は、タイマーの揺れでどの瞬間のレベルを拾うかに左右される
  （同じ 62.5ms の模擬では 3.9 秒）。比べるなら、条件を揃えた模擬の値を見る。
- 修正後でも理想より成立が少し少ない（123 回 対 141 回）。落ち込みの時間を 1 回 62.5ms 単位で足すため、
  許容 0.3 秒を超えるのが 5 回目（0.31 秒）になる。50ms ちょうどなら 7 回目（0.35 秒）である。
- 本物のタイマーの間隔: 9,588 回で平均 62.6ms、中央 61.4ms、95% 点 70.2ms、最大 452.6ms。名目の約 1.25 倍で、T210 の実測（10 秒の設定が 12.8 秒）と近い。

---

## 実行結果 (2026-11-24)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 564 件成功 / 0 件失敗 / 0 件スキップ（5 件追加）
- 計画からの逸脱: なし
