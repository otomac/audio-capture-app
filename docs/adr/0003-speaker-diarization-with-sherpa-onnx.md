# ADR-0003 — 話者ダイアライゼーションを sherpa-onnx で行い、ファイル文字起こし経路にだけ載せる

> **状態:** 承認済み
> **日付:** 2026-08-22
> **関連タスク:** [T115](../tasks/T115-speaker-diarization.md)

## 背景

文字起こし結果に「誰が話したか」が無い。会議録音を議事録へ起こす際、発話の切り替わりが
テキストからは判別できず、後段の作業（LLM による議事録生成を想定）が成立しない。

T115 は話者ダイアライゼーション（同一音声内で話者を `話者1` / `話者2` … と区別すること）を
追加する。話者の実名推定（Speaker Identification）は対象外である。

この変更は 2 つの点でアーキテクチャに触れるため ADR が要る。

1. **新しい外部ネイティブライブラリ（sherpa-onnx）を採用する。** 本プロジェクトは
   [CLAUDE.md](../../CLAUDE.md) の「ライブラリ追加は個別承認制」により、
   パッケージの追加を設計判断として扱う。
2. **[ADR-0001](./0001-baseline-architecture.md) の「Service のインターフェース抽象は不使用」と、
   元になった要求仕様が求める `ISpeakerDiarizationService` 等の抽象が正面衝突する。**
   どちらを採るかを決めないと実装できない。

## 現状

- 3 層構成（Models / ViewModels / Services）、DI コンテナなし、Service のインターフェース抽象なし
  （[ADR-0001](./0001-baseline-architecture.md)）。
- 文字起こしは `TranscriptionService` 1 クラスに 2 系統が同居している
  （[docs/spec/02_architecture.md](../spec/02_architecture.md)）。
  - **ライブ文字起こし** — 録音中に 20 秒チャンクを**ストリーミング**で処理し、
    マイク／スピーカーを別系統として `[マイク]` / `[スピーカー]` ラベル付きで `.txt` へ逐次追記する。
  - **ファイル文字起こし** — `TranscribeFileAsync` が音声ファイル全体を処理し `.transcript.txt` を作る。
- どちらの経路も音声を **16 kHz モノラル float** へ正規化してから Whisper に渡す
  （`DownmixResampleAppend`）。
- 無音カット（[T112](../tasks/T112-silence-cut-before-whisper.md)）により、Whisper には
  有声区間だけが渡る。各区間は自分の絶対時刻を持つため、無音を捨てても後続の時刻はずれない。
- テスト容易性は `InternalsVisibleTo` ＋ `internal static` の純粋関数で確保している
  （ADR-0001「結果」節）。

## 調査で分かった事実

実装方針を左右するため、`org.k2fsa.sherpa.onnx` 1.13.5 を捨てプロジェクトで実測した結果を残す。

| # | 事実 | 影響 |
|---|---|---|
| F1 | C# API は `OfflineSpeakerDiarization` / `OfflineSpeakerDiarizationConfig` / `FastClusteringConfig`（`NumClusters` と `Threshold`）/ `OfflineSpeakerDiarizationSegment`（`Start` / `End` は float 秒、`Speaker` は 0 始まりの int）。公式 example と同じ形で使える | 独自 ONNX 実装は不要 |
| F2 | `Process(float[])` は **音声全体を 1 つの配列で受け取る**。ストリーミング API は無い | ライブ経路には原理的に載らない |
| F3 | `SampleRate` プロパティが segmentation モデルの要求レートを返す | 16 kHz 一致をコードで検証できる |
| F4 | **モデルファイルが存在しない場合、ネイティブ側は NULL ハンドルを返すが C# ラッパーは検査しない。**その後 `SampleRate` を読んだ時点でアクセス違反（0xC0000005）となり、**.NET の catch を通らずプロセスが即死する** | 生成前の `File.Exists` 検査が必須 |
| F5 | モデルファイルが**存在するが壊れている**場合は `SEHException` が飛ぶ（catch 可能・プロセスは生存） | try/catch で拾える |
| F6 | メタパッケージは 9 プラットフォーム分のネイティブランタイム（計 約 330 MB）を推移的に引き込む。win-x64 に必要なのは `onnxruntime.dll` ＋ `sherpa-onnx-c-api.dll` の 約 21 MB のみ | 不要ランタイムの除外が必要 |
| F7 | 不要 RID の `PackageReference` に `ExcludeAssets="all"` を直接指定すると、ビルド出力は `runtimes/win-x64` だけになる（実測 22 MB） | 除外手段として成立する |

## 選択肢

### 争点 1 — Diarization エンジンをどう調達するか

#### 案 A — sherpa-onnx の公式 .NET API を使う（採用）

- **内容:** `org.k2fsa.sherpa.onnx` を参照し、`OfflineSpeakerDiarization` をそのまま使う。
- **利点:** 完全ローカルで動く。公式 API なのでモデル形式とクラスタリングの面倒を見なくてよい。
  segmentation / embedding モデルを差し替えられる。
- **欠点:** ネイティブ依存が 1 つ増える。F4 のとおり NULL ハンドル検査が無く、
  誤用するとプロセスごと落ちる。配布物が 約 21 MB 増える。
- **影響範囲:** `Directory.Packages.props`、`AudioCaptureApp.csproj`、Services 層に 1 クラス追加。

#### 案 B — ONNX Runtime を直接叩いて自前で実装する

- **内容:** `Microsoft.ML.OnnxRuntime` を入れ、pyannote の後処理とクラスタリングを自前で書く。
- **利点:** 依存が汎用ライブラリ 1 つで済む。ネイティブ API の作法に縛られない。
- **欠点:** segmentation の後処理・埋め込みの窓分割・クラスタリングをすべて再実装することになり、
  実装量と正しさのリスクが桁違いに大きい。本プロジェクトの規模に見合わない。
- **影響範囲:** Services 層に多数のクラス。

#### 案 C — Python の pyannote.audio を別プロセスで動かす

- **利点:** 精度の実績がある。
- **欠点:** Python ランタイムの同梱・配布が必要になり、単体 exe で完結する現在の配布形態が崩れる。
- **影響範囲:** 配布・ビルド全体。

### 争点 2 — どの経路に載せるか

#### 案 A — ファイル文字起こし経路だけに載せる（採用）

- **内容:** `TranscribeFileAsync` にのみ Diarization を組み込む。ライブ経路は一切変更しない。
- **利点:** F2 の制約と素直に一致する。ライブ経路（T117 / T120 / T126 / T127 / T129 が積み重なった
  最も壊れやすい部分）に手を入れずに済む。無効時は既存経路がそのまま走るので後方互換が自明になる。
- **欠点:** 録音した音声に話者を付けたい場合、録音後にファイル文字起こしを掛け直す操作が要る。
- **影響範囲:** `TranscriptionService.TranscribeFileAsync` とその下位メソッド。

#### 案 B — ライブ経路にも載せる

- **欠点:** F2 により不可能。20 秒チャンクごとに Diarization を掛けるとチャンク間で
  話者 ID の同一性が保てず（`SpeakerId` は 1 回の `Process` 内でしか意味を持たない）、
  「同一話者が再登場したら同じ ID」という完了条件を満たせない。
- 却下する。

#### 案 C — 何もしない

話者情報が無いまま。議事録生成の前提が満たせない。却下。

### 争点 3 — ADR-0001（抽象化なし）との整合をどう取るか

#### 案 A — 具象クラス ＋ `internal static` の純粋関数で責務分離する（採用）

- **内容:** `ISpeakerDiarizationService` / `ITranscriptDiarizationMerger` といったインターフェースは
  作らない。代わりに
  - `SpeakerDiarizationService`（具象・`IDisposable`）に sherpa-onnx 依存を閉じ込める
  - `TranscriptDiarizationMerger` を `internal static` の純粋関数にする
  で、要求仕様が求める**責務の分離**だけを満たす。
- **利点:** ADR-0001 と矛盾しない。実装が 1 つしかない抽象を増やさない。
  Merger は純粋関数なのでモックなしで厚くテストできる（ADR-0001 が想定したやり方そのもの）。
  将来 Diarization エンジンを差し替えるときも、差し替え点は 1 クラスに閉じている。
- **欠点:** `TranscriptionService` のユニットテストで Diarization をモック差し替えできない。
  これは ADR-0001 が既に受け入れているコストと同種である。
- **影響範囲:** Services 層。

#### 案 B — 要求仕様どおりインターフェースを導入する

- **利点:** モック差し替えができる。
- **欠点:** ADR-0001 を実質的に覆すことになり、「Service だけ 3 つインターフェース化されていて
  他は違う」という**最も悪い混在状態**（ADR-0001 案 C が警告したもの）を作る。
  覆すなら全 Service を対象に別 ADR で議論すべきで、本タスクのついでにやることではない。
- 却下する。

## 決定

**争点 1 は案 A、争点 2 は案 A、争点 3 は案 A を採用する。**

すなわち — **sherpa-onnx の公式 .NET API を使い、ファイル文字起こし経路にだけ Diarization を載せ、
インターフェース抽象は作らず具象クラスと `internal static` 純粋関数で責務を分ける。**

決め手：

- **F2（ストリーミング API が無い）が経路の選択を決めている。** 設計の好みではなく、
  ライブ経路に載せる案は技術的に成立しない。
- **依存の増減** — 案 B / C は実装量とリスクが規模に見合わない。ネイティブ依存 1 つの追加で済む
  案 A が最も安い。
- **既存規範との整合** — ADR-0001 を本タスクの都合で曲げない。要求仕様が求めているのは
  「責務の分離」であって「インターフェース」ではないため、純粋関数で満たせる。

あわせて、次の 3 点を本 ADR の一部として固定する。

- **N1 — 不要 RID のネイティブランタイムは `ExcludeAssets="all"` で除外する。**
  F6 / F7 による。本アプリは `net10.0-windows` の Windows 専用であり、
  linux / osx / android / win-x86 / win-arm64 のバイナリを配布物に含める理由が無い。
- **N2 — モデルファイルの存在検査を `SpeakerDiarizationService` 側で必ず行う。**
  F4 により、これを省くとプロセスが catch 不能に落ちる。ネイティブ API への信頼を前提にしない。
- **N3 — モデル 1 組をアプリ実行中で使い回し、`lock` で直列化する。**
  sherpa-onnx の `OfflineSpeakerDiarization` はスレッド安全性が保証されていないため、
  同一インスタンスの同時利用を排他制御で防ぐ。本アプリはファイル文字起こしを同時に 1 つしか
  走らせないため、実質的な待ちは発生しない。

## 結果

- **良くなること:**
  - `.transcript.txt` の各行に話者が付き、議事録生成の前提が満たせる。
  - Whisper と Diarization が互いに依存しないため、どちらのエンジンも独立して差し替えられる。
  - マージ規則が純粋関数として切り出され、境界条件をテストで固定できる。
- **悪くなること・受け入れるコスト:**
  - **ネイティブ依存が 1 つ増える。** 配布物が 約 21 MB 増える。F4 のような
    「ラッパーが守ってくれない」箇所をこちら側で守り続ける責任が生じる。
  - **Diarization 有効時、ファイル文字起こしはデコード済み PCM 全体をメモリに載せる。**
    16 kHz モノラル float で **約 230 MB / 時間**。F2 の制約上これは回避できない。
    無効時は従来どおりストリーミングで処理するため増えない。
  - **Diarization 有効時、`SegmentTranscribed` による画面への逐次表示がマージ完了後にまとまる。**
    話者の割り当てはタイムライン全体が揃ってからでないと確定できないため。
    進捗表示（REQ-TRX-FILE-06）は従来どおり処理中も動く。
  - **録音中のライブ文字起こしには話者が付かない。** 争点 2 案 A の直接の帰結。
  - `TranscriptionService` が扱う外部リソースが 1 つ増える。
    [20-architecture-standards.md §6](../harness/20-architecture-standards.md#6-構造が壊れかけているサイン)
    の「1 Service が 3 つ以上の無関係な外部リソースを扱う」に接近する。
    ただし sherpa-onnx への依存は `SpeakerDiarizationService` に閉じており、
    `TranscriptionService` は**任意の協調オブジェクトとして受け取るだけ**にする。
- **後戻りのしやすさ:** 高い。Diarization は追加された経路であり、
  設定 1 つ（`SpeakerDiarizationEnabled = false`）で従来動作に戻る。
  パッケージ参照とサービス 1 クラス、および `TranscribeFileAsync` の分岐を消せば撤去できる。

## 追随して更新するもの

- [x] `docs/spec/01_requirements.md` — §9 に `REQ-TRX-DIA-01`〜`10` を追加、REQ-TRX-07 に話者欄を追記
- [x] `docs/spec/02_architecture.md` — Services 層に `SpeakerDiarizationService` / `TranscriptDiarizationMerger` を追加、ファイル文字起こしのデータフローを記述
- [x] `docs/spec/03_class_diagram.md` — 上記 2 クラスと `SpeakerSegment` / `TranscriptSegment` / `SpeakerAttributedSegment` を追加
- [x] `docs/spec/04_sequence_diagram.md` — Diarization 有効時のファイル文字起こしの順序を追加
- [x] `docs/harness/20-architecture-standards.md` — 「意図的にやっていないこと」に本 ADR の争点 3 を反映
- [x] `README.md` — モデルの入手・配置・設定・ライセンスの確認方法（要求仕様 §29）
- [ ] `CLAUDE.md` — アーキテクチャ方針そのものは変わらないため変更しない
