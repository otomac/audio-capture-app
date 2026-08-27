# T117 — 録音停止時に長時間ブロックし異常終了する

> **状態:** 完了 — 2026-08-16
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **PR:** T116 と同一 PR（T116 が持ち込んだ劣化を含むため分離しない）

## 1. 目的

スピーカー（ループバック）から 25 秒程度録音・文字起こしすると、停止時にアプリが
長時間ブロックし、ときどきプロセスが異常終了する問題を直す。

## 2. 調査結果

### 2.1 クラッシュの直接原因

Windows アプリケーションログに**同一の例外が 4 回**記録されていた
（2026-08-15 18:10 / 18:14、2026-08-16 13:46 / 14:08）。

```
System.Exception: Cannot dispose while processing, please use DisposeAsync instead.
   at Whisper.net.WhisperProcessor.Dispose()
   at TranscriptionService.StopSession()
   at AudioCaptureService.StopRecording()
   at MainViewModel.<StopRecordingAsync>b__51_0()
   ...
   at CommunityToolkit.Mvvm.Input.AsyncRelayCommand.AwaitAndThrowIfFailed(Task executionTask)
```

`StopSession` は次の順で動く。

```
_isRunning = false
_thread.Join(30秒)          ← バックログを捌き切るまで戻らない
  タイムアウト → _cts.Cancel()
  _thread.Join(5秒)         ← ネイティブ処理は即座に止まらない
state.Processor?.Dispose()  ← まだ処理中だと Whisper.net が投げる
```

`Task.Run` 内で投げられた例外を `AsyncRelayCommand` が Dispatcher 上で再スローし、
**未処理例外としてプロセスを終了させる**。「固まる」と「異常終了」は同じ 1 本の経路で、
バックログが 30 秒以内に捌ければ固まるだけ、捌けなければ落ちる。

### 2.2 これは既存不具合である

**2026-08-15 18:10 / 18:14 の 2 件は .NET 8.0.30 で発生している。**
T108（.NET 10 化）のマージ前、T116 を書く前であり、**本不具合は T116 以前から存在**していた。

### 2.3 ただし T116 が悪化させていた

1. **窓ごとの無音判定**（T116）— 以前は「無音」として捨てられていたチャンクが
   Whisper に届くようになり、実処理量が増えた。
2. **`TranscriptionLoop` の内側ループ**（T116）— 「取り出せるだけ処理する」に変えた際、
   継続条件に `_isRunning` を入れ忘れていた。**停止要求後もバックログを全部捌き切るまで抜けない。**
   変更前は 1 tick あたり 1 チャンクで `while (_isRunning)` を毎回通っていたため停止が速かった。

2 は明確に T116 が持ち込んだ劣化。よって同一 PR で閉じる。

### 2.4 UI スレッドが固まる経路

[MainWindow.xaml.cs:17](../../AudioCaptureApp/MainWindow.xaml.cs#L17) の
`Closed += (_, _) => Dispose()` から `MainViewModel.Dispose()` →
`AudioCaptureService.Dispose()` → `StopRecording()` → `StopSession()` が
**UI スレッド上で**呼ばれる。録音中にウィンドウを閉じると UI スレッドが最大 35 秒ブロックし、
アプリ全体が完全に固まる。

### 2.5 実測 — 再現とバックログの影響

実オーディオデバイスを使わず、`TranscriptionService` へ合成音を直接投入して再現した。

| 投入音声 | ランタイム | `StopSession` 所要 |
|---|---|---|
| 120 秒（連続） | Vulkan | 20.5 秒ブロック |
| 420 秒（連続） | Vulkan | 28.3 秒ブロック |

バックログに比例してブロック時間が伸び、30 秒を超えると §2.1 のクラッシュ経路に入る。

さらに重要な実測として、**Whisper の 1 回の推論コストは入力長にほぼ依存しない**
（whisper.cpp が内部で 30 秒窓へパディングするため）。T116 の計測より:
0.2 秒入力 = 7745ms、2.0 秒入力 = 7587ms（CPU）。
つまり**チャンクを細かく分けるほど総コストが線形に増える**。

## 3. 修正

| # | 内容 |
|---|---|
| 1 | `DisposeProcessorSafely` を追加。ワーカーが抜けていなければ破棄を見送り、例外は握って `Error` に変換する。**どんな場合もプロセスを落とさない** |
| 2 | `TranscriptionLoop` の内側ループの継続条件に `_isRunning` を追加（T116 の劣化の是正） |
| 3 | `TakeNextChunk` がバッファ全体ではなく **20 秒ぶんだけ**切り出すよう変更。1 回の Whisper 呼び出しが数分ぶんになってキャンセル不能になるのを防ぐ |
| 4 | `MainViewModel.StopRecordingAsync` の `Task.Run` を try/catch で保護し、`AsyncRelayCommand` 経由の再スローを断つ |

キャンセル後の待機を 5 秒 → **10 秒**へ延ばした（ネイティブ処理が抜けきる確率を上げ、
破棄を見送ってリークする経路に入りにくくするため）。

### スコープ境界

**やらないこと**

- **`StartMicMonitor` の例外処理。** 同じ調査で見つけた別クラッシュだが独立した不具合 → **T118**
- **GPU/CPU 切替が効いていない疑い。** 要追加調査 → **T119**
- **ウィンドウクローズを非同期化すること。** §2.4 の UI ブロックは修正 1〜3 で
  大幅に短縮されるが、経路自体は同期のまま残す（別途判断）

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-04 / 05 の改訂、REQ-TRX-LIVE-10 / 11 の新設
- [-] その他の章 — 影響なし

## 5. アーキテクチャへの影響

なし（`TranscriptionService` 内部の停止手順と例外境界の是正のみ）。ADR: **不要**。

## 6. テスト

- **`ChunkStartElapsed_AfterTakingOneChunk_RemainderStartsWhereChunkEnded`** —
  20 秒ぶんだけ切り出したあと、残りバッファの先頭時刻が切り出したチャンクの直後から
  連続していること（修正 3 の不変条件）

> **テストで守れない範囲:** `StopSession` のタイムアウト経路と `Dispose` の例外は
> Whisper のネイティブ処理を実際に走らせないと再現できないためユニットテスト不可。
> 下記の実測プローブで担保した。

---

## 実行結果 (2026-08-16)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **46** 件成功 / **0** 件失敗 / **0** 件スキップ

### 修正後の実測（同じプローブ）

| シナリオ | 結果 |
|---|---|
| 連続 420 秒のバックログ → 停止 | **30.1 秒 / 例外なし**（30 秒で打ち切り→キャンセル→正常に解放） |
| 25 秒・断続ループバック（0.7 秒の途切れ × 11 回）→ 停止 | **2.9 秒 / 例外なし** |

報告に近い「25 秒・ループバック」の条件では停止が **2.9 秒**に収まった。

### 残るリスク（要観察）

§2.5 のとおり **Whisper の推論コストは入力長にほぼ依存しない**ため、
T116 のギャップ分割が細かいチャンクを多数作ると、総コストが線形に増える。

- GPU（Vulkan）では 1 チャンクあたり数百 ms のため、上記実測どおり追いつく。
- **CPU フォールバック時は 1 チャンクあたり約 8 秒**になるため、
  25 秒の録音で 12 チャンクできると 96 秒ぶんの処理となり、バックログが膨らむ。

根本的に避けるなら、ギャップを**分割ではなく無音で埋める**設計に変えるのが有効
（無音チャンクは `IsSilent` が Whisper に渡さず捨てるため、追加コストがほぼ無い）。
本タスクでは承認済みスコープ（修正 1〜4）に留め、この設計変更は提案として残す。
