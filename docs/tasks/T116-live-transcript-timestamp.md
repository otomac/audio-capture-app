# T116 — ライブ文字起こしの記録時刻が実時刻とずれる

> **状態:** 完了 — 2026-08-16
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

ライブ文字起こしの `.txt` に記録される時刻が、実際に発話された時刻から大きくずれる。
無音／ミュート区間で時計が進まないことが原因。あわせて、短い発話や小音量の発話が
丸ごと捨てられて出力されない問題も直す。

---

## 2. 調査結果

### 2.1 時刻はどう計算されているか

時刻は壁時計ではなく **「文字起こしバッファへ投入されたサンプル数の累積」** から作られている。

`TranscriptionService.ProcessChunk`（[TranscriptionService.cs:467-484](../../AudioCaptureApp/Services/TranscriptionService.cs#L467-L484)）:

```csharp
var startTime = _sessionStartTime + state.ChunkOffset + segment.Start;
...
state.ChunkOffset += TimeSpan.FromSeconds((double)samples.Length / TargetRate);
```

`ChunkOffset` は「そのソースが受け取った音声の総再生時間」であり、
**壁時計とは独立**している。両者が一致するのは「音声が途切れなく供給され続ける」場合だけ。

### 2.2 根本原因 — サンプルが供給されない区間が 2 つある

`ChunkOffset` が進むのは `AddSamples` が呼ばれたときだけ。ところが以下の 2 ケースで
`AddSamples` が **一度も呼ばれない**。その間 `ChunkOffset` は凍結し、タイムラインが縮む。

#### 原因 A — ミュート中は文字起こしへサンプルを渡していない（マイク・スピーカー共通）

[AudioCaptureService.cs:211-218](../../AudioCaptureApp/Services/AudioCaptureService.cs#L211-L218)（マイク）と
[AudioCaptureService.cs:380-387](../../AudioCaptureApp/Services/AudioCaptureService.cs#L380-L387)（スピーカー）:

```csharp
if (_isMicMuted)
{
    // 録音バッファには無音を書き込む
    _micBuffer.AddSamples(_micSilenceBuffer, 0, e.BytesRecorded);
    _micPeakLevel = 0f;
}
else
{
    _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    ...
    if (_isWriting && _transcriptionService != null)
        _transcriptionService.AddSamples(AudioSourceType.Mic, floats, floats.Length);  // ← else 節にしかない
}
```

**録音バッファ（MP3）には無音が書き込まれるのに、文字起こしには何も渡らない。**
結果、MP3 のタイムラインと `.txt` のタイムラインが乖離する。

#### 原因 B — スピーカーは再生音が無いとコールバック自体が発火しない

WASAPI のループバックキャプチャは、レンダーエンドポイントがアイドルのとき
`IAudioCaptureClient::GetBuffer` が `AUDCLNT_S_BUFFER_EMPTY` を返し **パケットを生成しない**。
NAudio は `DataAvailable` を発火しないため、`AddSamples` も呼ばれない。

これは T110（スピーカーメーター常時化）の調査で確認した挙動と同一の原因である
（T110 ではメーターが固着する問題として現れ、無音タイムアウトで対処した）。

### 2.3 報告された事象との対応

| 事象 | 原因 | 説明 |
|---|---|---|
| マイクとスピーカーが同じ時刻になった（両方 09:09:17 付近） | **B** | `ChunkOffset` はソースごとに独立して 0 から始まる。マイクは録音開始から連続で供給される一方、スピーカーは再生が始まるまで 1 サンプルも来ない。よってスピーカーの最初のセグメントは実際の発生時刻に関わらず `sessionStart + 0` になる |
| スピーカー再生後にマイクで話したら、冒頭のマイク時刻の直後になった | **A** | ログの「今 ミュートしました」の通りマイクをミュートしていた。ミュート中 `ChunkOffset` が凍結し、解除後に凍結時点から再開した |
| スピーカーを 2 分無音にして再開したら、止めた時刻の直後になった | **B** | 2 分間コールバックが来ず `ChunkOffset` が凍結 |

いずれも「実時刻が進んでも `ChunkOffset` が進まない」ことの直接の帰結であり、
報告された 3 つの事象は**すべて同一の根本原因**で説明できる。

### 2.4 短い発話・小音量の発話が出力されない（別原因）

これは時刻とは独立した第 2 の不具合。`IsSilent`
（[TranscriptionService.cs:427-438](../../AudioCaptureApp/Services/TranscriptionService.cs#L427-L438)）は
**20 秒チャンク全体の RMS** で判定する（`BufferThresholdSamples = TargetRate * 20`）。

```csharp
double rms = Math.Sqrt(sumSquares / samples.Length);
return rms < 0.01;   // -40dB
```

チャンク内の一部だけが発話の場合、RMS は無音部で希釈される。
発話区間 `d` 秒・その区間の RMS が `r` なら、チャンク全体の RMS は `r * sqrt(d / 20)`。

| 発話長 | r=0.02 | r=0.03 | r=0.05 |
|---|---|---|---|
| 0.5 秒 | 0.00316 ✕ | 0.00474 ✕ | 0.00791 ✕ |
| 1 秒 | 0.00447 ✕ | 0.00671 ✕ | 0.01118 ○ |
| 2 秒 | 0.00632 ✕ | 0.00949 ✕ | 0.01581 ○ |
| 3 秒 | 0.00775 ✕ | 0.01162 ○ | 0.01936 ○ |
| 5 秒 | 0.01000 ○ | 0.01500 ○ | 0.02500 ○ |

（✕ = 閾値 0.01 未満 ＝ **チャンクごと破棄され Whisper に渡らない**）

通常の会話音声の RMS は概ね 0.02〜0.05。つまり **20 秒中の発話が 2〜3 秒以下だと
チャンクまるごと捨てられる**。報告の「9:11:30 / 9:12:00 / 9:12:30 / 9:13:00 付近の短い発話が
出力されない」はこれで説明できる。マイクはスピーカーより音量が小さいため、より起きやすい。

加えて末尾の残りバッファは **1 秒以上** ないと処理されない
（[TranscriptionService.cs:413](../../AudioCaptureApp/Services/TranscriptionService.cs#L413),
[:299](../../AudioCaptureApp/Services/TranscriptionService.cs#L299)）。
「9:15:00 過ぎの終了の発話が出ない」はこれ、または上記の希釈のいずれか。

---

## 3. 修正方針（承認をお願いしたい内容）

### 方針 1 — 供給されない区間を「無音」として時計に反映する

`ChunkOffset` を壁時計と一致させるため、**サンプルが来なかった時間を無音として埋める**。
MP3 側は既に無音が書き込まれており、`.txt` を MP3 のタイムラインに合わせる形になる。

実装は `TranscriptionService.AddSamples` 側で行う（**キャプチャ側の分岐は変えない**）:

1. ソースごとに「最後にサンプルを受け取った壁時計時刻」を保持する。
2. `AddSamples` 呼び出し時に、前回からの経過壁時計時間と、その間に受け取った音声時間を比較する。
3. 差が閾値（既定 200ms）を超えていたら、その差を **ギャップ** として扱う。

ギャップの扱いは **「バッファをフラッシュして、次チャンクの壁時計基準を打ち直す」** 方式を採る:

- 各チャンクに「そのチャンク先頭の壁時計時刻」を持たせ、時刻は
  `chunkWallStart + segment.Start` で計算する（`ChunkOffset` の累積をやめる）。
- ギャップを検出したら、溜まっているバッファを 20 秒未満でも 1 チャンクとして確定し、
  新しいチャンクの `chunkWallStart` を「今」にする。

**無音サンプルを実体として詰めない**理由: 2 分ミュートすると 1.9M サンプル（約 7.7MB）を
生成し、文字起こしループは 1 tick あたり 1 チャンクしか処理しないため、
無音チャンクの消化に約 6 秒かかって以降の発話が遅延する。時刻情報だけを持たせれば足りる。

**副次的な効果:** ギャップでフラッシュされるため、発話直後に無音が来た場合はその発話が
「短いチャンク」として独立し、方針 2 と合わせて希釈されなくなる。

### 方針 2 — 無音判定をチャンク全体の平均から「区間ごとの最大」に変える

`IsSilent` を、チャンクを短い窓（既定 100ms）に切って **窓ごとの RMS の最大値**で判定する形に変える。
1 つでも閾値を超える窓があればチャンクを Whisper に渡す。

- 20 秒中 0.5 秒の発話でも、その窓の RMS は希釈されないため通る。
- 完全な無音チャンクは全窓が閾値未満なので従来どおり破棄され、ハルシネーション抑止は維持される。
- 閾値は現行の 0.01 を出発点とし、Options 相当（定数の一元管理）に置く。

あわせて末尾残りバッファの最小長を 1 秒から **0.2 秒** へ下げる。
ただし Whisper は極端に短い入力を扱えない可能性があるため、**1 秒未満のチャンクは
無音パディングで 1 秒に伸ばして渡す**（要検証）。

### スコープ境界

**やること**
- 上記方針 1・2
- 対応するユニットテストの追加

**やらないこと（重要）**
- **キャプチャ側（`AudioCaptureService`）のミュート分岐の変更。** 原因 A の対処は
  「ミュート中も文字起こしへ実音声を渡す」ではない。それはミュートの意味を壊す。
  時刻の埋め合わせは `TranscriptionService` 側で行う。
- **ファイル文字起こし（`TranscribeFileCoreAsync`）の時刻計算の変更。**
  ファイルは音声が連続しているためギャップは発生せず、本不具合の影響を受けない。
  ただし `IsSilent` の変更（方針 2）は共通関数のため両方に効く。
- **チャンク内の無音区間を切り出して Whisper への入力自体を短くすること。**
  これは T112（要件 4）の範囲。本タスクは「捨てすぎを直す」までで、
  「無音を切ってから渡す」は T112 で行う。
- **20 秒というチャンク長の変更。**

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - REQ-TRX-06 を改訂（窓ごとの無音判定）
  - REQ-TRX-08 を新設（1 秒未満のパディング）
  - REQ-TRX-LIVE-04 / 05 を改訂（複数チャンク処理、残り 1 秒 → 0.2 秒）
  - REQ-TRX-LIVE-06 / 07 / 08 / 09 を新設（実経過時間、ギャップ分割、時刻逆算、状態リセット）
- [-] `docs/spec/04_sequence_diagram.md` — ライブ文字起こしのシーケンス図は
  チャンク確定条件の粒度まで描いていないため影響なし
- [-] `docs/spec/03_class_diagram.md` — `IsSilent` の公開シグネチャは変わらず
  （オーバーロード追加のみ）、その他は `internal` の純粋関数のため影響なし

## 5. アーキテクチャへの影響

3 層構成・依存方向・スレッドモデルはいずれも不変。`TranscriptionService` 内部の
時刻計算とバッファ確定条件の変更に留まる。

- ADR: **不要**

## 6. 未解決の質問（解決済み）

1. **`.txt` の時刻を MP3 のタイムラインに合わせる方針でよいか。** → **承認済み。** 既定案どおり実装。
2. **1 秒未満のチャンクを無音パディングで 1 秒に伸ばす扱いでよいか。** → **承認済み。実測で裏付けを取った（下記）。**

### Whisper.net の短い入力に対する挙動（実測）

`ggml-small.bin` を CPU ランタイムで読み込み、0.05〜2.0 秒の合成音を
そのまま渡した場合とパディングした場合で比較した。

| 入力長 | そのまま | 1.0 秒へパディング |
|---|---|---|
| 0.05 秒 (800) | OK / **segments=0** | OK / segments=1 |
| 0.10 秒 (1600) | OK / **segments=0** | OK / segments=1 |
| 0.20 秒 (3200) | OK / segments=1 | OK / segments=1 |
| 0.50 秒 (8000) | OK / segments=1 | OK / segments=1 |
| 0.90 秒 (14400) | OK / segments=1 | OK / segments=1 |
| 2.00 秒 (32000) | OK / segments=1 | OK / segments=1 |

**判明したこと（当初の想定とは異なる）:**

- Whisper.net は短い入力でも **例外を投げない**。「扱えずに落ちる」という当初の想定は誤りだった。
- 代わりに **0.2 秒未満では 1 件もセグメントを返さず黙って捨てられる**。
- パディングするとセグメントが返る。

したがって `PadToMinimum` は必要だが、理由は「クラッシュ回避」ではなく
**「0.2 秒未満の発話が黙って消えるのを防ぐため」** である。ギャップ分割では
0.2 秒未満のチャンクが発生しうるため、この対策は実際に効く。

## 7. 前提

- 本不具合の再現・修正確認には実機のマイクとスピーカー再生が必要。
  検証環境では `WasapiLoopbackCapture` が起動できない（T110 参照）ため、
  **エンドツーエンドの確認は手動に依存する。** ユニットテストは時刻計算と
  無音判定のロジックを純粋関数として切り出して担保する。

---

## 8. テスト一覧

無音判定・ギャップ検出・時刻逆算・パディングを純粋関数として切り出し、13 件を追加した。

- **`IsSilent_ShortSpeechInLongSilence_ReturnsFalse`** — 20 秒中 1.5 秒・RMS 0.03 の発話が
  捨てられないこと（報告事象の回帰テスト）
- **`IsSilent_ShortSpeechInLongSilence_WholeChunkAveragingWouldDropIt`** — 修正前の判定方式
  （窓＝チャンク全体）では同じ信号が捨てられていたことを固定する
- **`IsSilent_VeryShortSpeechInLongSilence_ReturnsFalse`** — 窓長より短い 50ms の発話も拾う
- **`IsSilent_LongQuietRoomNoise_StillReturnsTrue`** — 暗騒音は従来どおり無音（ハルシネーション抑止の維持）
- **`IsSilent_EmptyChunk_ReturnsTrue`** — 空チャンク
- **`ShouldSplitOnGap_ContiguousAudio_ReturnsFalse`** — 通常の連続供給では分割しない
- **`ShouldSplitOnGap_LongSilenceGap_ReturnsTrue`** — 2 分の途切れを検出する
- **`ShouldSplitOnGap_JitterBelowThreshold_ReturnsFalse`** — スケジューリング遅延で分割しない
- **`ShouldSplitOnGap_EmptyBuffer_ReturnsFalse`** — 空バッファは分割対象外
- **`ChunkStartElapsed_SubtractsBufferedDuration`** — 末尾からバッファ長を引いて先頭時刻を得る
- **`ChunkStartElapsed_NeverGoesNegative`** — 記録時刻が録音開始より前にならない
- **`PadToMinimum_ShorterThanMinimum_PadsWithSilenceAtEnd`** — 先頭を保ったまま末尾を無音で伸ばす
- **`PadToMinimum_AlreadyLongEnough_ReturnsSameInstance`** — 十分な長さなら再確保しない

> **テストで守れない範囲:** `_sessionClock` と実キャプチャを絡めたエンドツーエンドの時刻検証は
> 実機のマイク／スピーカー再生を要するためユニットテスト不可（検証環境では
> `WasapiLoopbackCapture` が起動できない）。**手動確認が必要。**

---

## 実行結果 (2026-08-16)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**
- `dotnet test AudioCaptureApp.slnx -c Debug` : **45** 件成功 / **0** 件失敗 / **0** 件スキップ
  （既存 32 件 ＋ 新規 13 件）

### 手動確認をお願いしたいこと

1. マイクで数秒話す → スピーカーで再生 → **両者の時刻が別々になり、実時刻に一致する**
2. マイクをミュート → 数分後に解除して発話 → **解除後の実時刻で記録される**
3. スピーカー再生を数分止めて再開 → **再開した実時刻で記録される**
4. 短い発話（1〜2 秒）や小さめの声が **テキストに出力される**
5. 完全な無音区間で **ハルシネーションが増えていない**（§9 の既知のトレードオフ）

### 9. 既知のトレードオフ（T112 で解消予定）

無音判定を「窓ごとの最大」に緩めたため、20 秒チャンクの中に短い物音（キーボード音・咳など）が
1 つでもあると、チャンク全体が Whisper に渡るようになった。
**その結果、無音部でのハルシネーションが一時的に増える可能性がある。**

本タスクの目的は「捨てすぎを直す」ことであり、根本解決は
**T112（無音区間を切り出してから Whisper に渡す）** で行う。
T112 を入れると、渡す音声そのものから無音が除かれるためこのトレードオフは解消する。
