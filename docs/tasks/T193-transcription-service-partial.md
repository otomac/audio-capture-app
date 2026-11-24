# T193 — `TranscriptionService.cs` を機能単位の partial に分ける

> **状態:** 完了 — 2026-11-25
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

`TranscriptionService.cs` は 2,470 行あり、ライブの確定パス・ファイル文字起こし・話者付きの書き出し・無音カット・モデル読み込みが
1 ファイルに同居している。ViewModel と同じ「1 ファイル 500 行が目安」に寄せて、機能単位の `partial` に分ける。
**振る舞いは変えない**（メンバーの本文・コメント・アクセス修飾子は一字も変えず、置き場所だけを動かす）。

## 2. 分け方

T186 / T188 で先に切り出した `TranscriptionService.Provisional.cs`（速報）・`.Context.cs`（文脈プロンプト）はそのまま。

| ファイル | 中身 | 元の行 |
|---|---|---|
| `TranscriptionService.cs` | 共通の土台: `AudioSourceType`・`LiveChunkCompletion`、`PendingChunk` / `SourceState`、フィールド・定数・イベント・設定プロパティ、`DownmixResampleAppend`（ライブとファイルの両方が使う変換）、`Dispose` | 1–14, 60–66, 203–299, 304–323, 351–398, 816–847, 2451–2471 |
| `TranscriptionService.Model.cs` | モデル読み込み: 言語の指定、GPU の判定（ネイティブログの解析）、`LoadModel`、flash attention、`UnloadModel` / `DisposeProcessor` | 400–620, 2435–2449 |
| `TranscriptionService.Live.cs` | ライブのセッション: `RegisterSource` / `StartSession` / `AddSamples`、ギャップでの分割、停止（`RequestAbort` / `StopSession`）、滞留の集計、`DisposeProcessorSafely` | 622–785, 2291–2433 |
| `TranscriptionService.LiveWorker.cs` | 確定パスのワーカー: `TranscriptionLoop`、音源の手番、チャンクの取り出しと区切りの判定、`ProcessChunk` / `TranscribeRegion`、行の追記 | 1582–1795, 2288–2289, 1838–1936, 2129–2286 |
| `TranscriptionService.SilenceCut.cs` | 無音カット: 窓・足切りの定数、`RegionStart` / `PadToMinimum`、`TrailingSilenceSamples`、`SplitVoicedRegions` 以下 | 301–302, 325–349, 787–814, 1797–1836, 1938–2127 |
| `TranscriptionService.File.cs` | ファイル文字起こしの入口と従来経路: `TranscribeFileAsync`、factory の共有判定、`TranscribeFileCoreAsync`、フェーズ名、`ProcessFileChunkAsync` | 849–1073, 1547–1580 |
| `TranscriptionService.FileDiarization.cs` | 話者識別ありの経路: `TranscribeFileWithDiarizationAsync`、全体のデコード、トークンからの発話区間、話者付きの書き出し | 1075–1376 |
| `TranscriptionService.FileIo.cs` | 入出力ファイルまわり: 音声ファイルを開く、出力名（連番を含む）、作成日時と長さ、作りかけの出力の削除 | 1378–1545 |
| `SilenceCutOptions.cs` | `SilenceCutOptions`・`VoicedRegion` | 15–58 |
| `TranscriptionLanguages.cs` | `TranscriptionLanguage`・`TranscriptionLanguages` | 68–129 |
| `FileTranscriptionOptions.cs` | `FileTranscriptionProgress`・`FileTranscriptionOptions`・`FileTranscriptionOutcome`・`FileTranscriptionResult` | 131–201 |

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 範囲 | 起票時の「ファイル文字起こしと無音カットを先に」だけで止めず、全体を 500 行以下に割る。どれも置き場所を動かすだけで、1 回のコミットで見比べられる |
| D2 | クラスの外の型 | `partial` ではなく型ごとの別ファイルにする（`LiveChunkOptions.cs` / `ProvisionalOptions.cs` と同じ並び）。`AudioSourceType` と `LiveChunkCompletion` は小さく、クラスと一体なので本体に残す |
| D3 | 移し方 | 行範囲を機械的に切り出す（本文を手で書き写さない）。各ファイルの `using` は IDE0005 で要るものだけに絞る |
| D4 | 2 か所から使う補助 | `DownmixResampleAppend`（ライブとファイル）は本体、`RegionStart` / `PadToMinimum` / `SliceRegion`（ライブとファイル）は無音カット側に置く |
| D5 | 初期化の順序 | ファイルをまたぐ `static readonly` / インスタンスのフィールド初期化子の実行順は C# では決まっていないが、どの初期化子もほかのフィールドを読まないので、分けても結果は変わらない（定数 `const` は順序に関係しない） |

## 4. 仕様書への影響

- [x] 変更なし（仕様非影響。`docs/spec/` が名指ししているファイルは `TranscriptionService.Provisional.cs` だけで、動かさない）

## 5. アーキテクチャへの影響

- ADR: 不要（同じクラスの `partial` 分割。層・依存・公開 API は変わらない）

## 6. 確認の方法

- 品質ゲート 3 つ（テストは追加しない。既存のテストが同じ件数で通ることを見る）
- 移す前後で、メンバーの行の集合が同じであること（ファイルの見出しコメント・`using`・クラスの宣言行を除いて、並べ替えた行の差分が無い）

## 7. 確認

**行の突き合わせ:** 元の `TranscriptionService.cs` と分けた後の 11 ファイルを、空行を除いて行ごとに並べ替えて比べた。
差は足したものだけだった（各ファイルの見出しコメント・`namespace`・`using`・`public partial class TranscriptionService` と波かっこ）。
元の行で消えたもの・書き換わったものは無い。

**`using`:** 各ファイルの `using` を 1 つずつ外してビルドし、外すとビルドが壊れるものだけを残した。外しても通ったのは
`TranscriptionService.Live.cs` の `using Whisper.net;` だけで、これは `DisposeProcessorSafely` の説明の
`<see cref="WhisperProcessor"/>` が参照している（このプロジェクトは XML ドキュメントを生成しないので、cref はビルドでは検査されない）。
元のファイルでも同じ `using` で解決していたので残した。

**行数（分けた後）:**

| ファイル | 行数 |
|---|---|
| `TranscriptionService.cs` | 236 |
| `TranscriptionService.Model.cs` | 249 |
| `TranscriptionService.Live.cs` | 317 |
| `TranscriptionService.LiveWorker.cs` | 486 |
| `TranscriptionService.SilenceCut.cs` | 295 |
| `TranscriptionService.File.cs` | 270 |
| `TranscriptionService.FileDiarization.cs` | 312 |
| `TranscriptionService.FileIo.cs` | 178 |
| `TranscriptionService.Provisional.cs`（変更なし） | 535 |
| `TranscriptionService.Context.cs`（変更なし） | 164 |
| `SilenceCutOptions.cs` / `TranscriptionLanguages.cs` / `FileTranscriptionOptions.cs` | 45 / 63 / 72 |

> **残り:** `TranscriptionService.Provisional.cs`（535 行）は目安を少し超えているが、T186 で速報だけを 1 ファイルに閉じる方針
> （ADR-0007）で切り出したもので、今回は触らない。

---

## 実行結果 (2026-11-25)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 567 件成功 / 0 件失敗 / 0 件スキップ（追加なし。分ける前と同じ件数）
- 計画からの逸脱: なし
