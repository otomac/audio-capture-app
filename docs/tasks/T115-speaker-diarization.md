# T115 — 話者ダイアライゼーション（Speaker Diarization）を sherpa-onnx で導入する

> **状態:** 完了 — 2026-08-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **ADR:** [ADR-0003](../adr/0003-speaker-diarization-with-sherpa-onnx.md)

## 1. 目的

文字起こし結果に「誰が話したか」を付ける。同一音声内で話者を `話者1` / `話者2` … と区別できれば
よく、実在の人物名への変換は後段（LLM による議事録生成）の責務とする。

## 2. スコープ境界

**やること**

- sherpa-onnx（`org.k2fsa.sherpa.onnx` 1.13.5）を導入し、win-x64 以外のネイティブランタイムを除外する
- `SpeakerDiarizationService`（sherpa-onnx 依存をここだけに閉じ込める）を追加する
- `TranscriptDiarizationMerger`（`internal static` の純粋関数）を追加する
- `TranscribeFileAsync` に Diarization 有効時の経路を足す
- 設定項目と、その clamp（検証）を追加する
- Merger の境界条件をユニットテストで固定する
- README にモデルの入手・配置・設定・ライセンス確認方法を書く

**やらないこと（重要）**

- **ライブ文字起こし（録音中）の経路には一切触れない。** sherpa-onnx の Diarization API は
  音声全体を要求するため載せられない（[ADR-0003](../adr/0003-speaker-diarization-with-sherpa-onnx.md) 争点 2）。
  `StartSession` / `AddSamples` / `TranscriptionLoop` / `ProcessChunk` / `StopSession` は無変更。
- **Speaker Identification（人物名の推定・声紋登録・個人認証）は実装しない。**
- **UI に話者ダイアライゼーションの切り替えや話者数の指定を追加しない。**
  有効化は `settings.json` の編集で行う（`SilenceRmsThreshold` 等と同じ扱い）。
  UI 導線が必要になったら別タスクで起票する。
- **Service のインターフェース抽象（`ISpeakerDiarizationService` 等）は作らない。**
  ADR-0001 を本タスクの都合で曲げない。
- **隣接セグメントの結合（同一話者の連続行をまとめる）は実装しない。** D6 参照。
- **モデルファイルはリポジトリに入れない。** 利用者が配置する。
- **GPU（CUDA / DirectML）での Diarization 実行は行わない。** `Provider` は既定の `cpu` のまま。

## 3. 決定事項

実装中に蒸し返さない。変更するときは本セクションを直してから。

| # | 選択が必要だった点 | 結論 |
|---|---|---|
| D1 | どの経路に載せるか | **ファイル文字起こしのみ。** ライブは技術的に不可（ADR-0003 争点 2） |
| D2 | 抽象化するか | **しない。** 具象クラス ＋ `internal static` 純粋関数で責務分離（ADR-0003 争点 3） |
| D3 | Whisper と Diarization の実行順 | **Diarization を先。** モデル不備を Whisper に数分掛ける前に判明させるため |
| D4 | 出力行の書式 | **`[時刻 - 時刻] [ラベル] [話者N] テキスト`。** 既存ラベルの後ろに足す。無効時は 1 文字も変えない |
| D5 | 重複長が同点のときの話者 | **話者 ID が小さい方。** 結果を決定的にするため（要求仕様 §23 Case 3） |
| D6 | 同一話者の連続セグメントを結合するか | **しない。** 既存出力は「Whisper セグメント 1 つ ＝ 1 行 ＋ 時刻」であり、結合すると時刻の粒度が落ちる。要求仕様 §14 も「必要に応じて」であり必須ではない |
| D7 | 不正な時刻（End < Start）の扱い | **`ArgumentException` を投げて reject する**（要求仕様 §23 Case 7）。呼び出し元が捕捉して「失敗」に落とす |
| D8 | 進捗表示の見せ方 | **フェーズ名付きの 2 フェーズ**（「話者識別中」→「処理中」）。各フェーズが 0% から進む。`IProgress<FileTranscriptionProgress>` へ型を変える |
| D9 | Diarization が失敗したときの扱い | **文字起こしごと中止する。** 話者欄が黙って欠けた成果物を作らない（REQ-TRX-DIA-11） |
| D10 | 不要 RID のネイティブを除外する方法 | **`ExcludeAssets="all"` を付けた直接 `PackageReference`。** 実測で出力が 約330MB → 22MB になる |
| D11 | `SpeakerDiarizationService` の所有者 | **`MainViewModel`。** 生成・破棄を持つ。`TranscriptionService` は引数で受け取るだけで保持しない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — §13 に `REQ-TRX-DIA-01`〜`12` を新設。`REQ-TRX-07`（話者欄）と
      `REQ-TRX-FILE-06`（2 フェーズ進捗）を改訂。`NFR-07` / `NFR-08` を追加
- [x] `docs/spec/02_architecture.md` — 技術スタック・レイヤー図・依存関係・スレッドモデル・データフロー §5.3
- [x] `docs/spec/03_class_diagram.md` — 新規 5 クラス ＋ 3 レコード、`AppSettings` の項目、関係
- [x] `docs/spec/04_sequence_diagram.md` — §6.1 を新設、§6 に分岐の注記

## 5. アーキテクチャへの影響

新しい外部ライブラリの採用と、ADR-0001 の「Service 抽象化なし」との整合判断を含むため **ADR 必須**。

- ADR: 要 → [docs/adr/0003-speaker-diarization-with-sherpa-onnx.md](../adr/0003-speaker-diarization-with-sherpa-onnx.md)（承認済み）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `Directory.Packages.props` | sherpa-onnx 本体と 9 つのランタイムパッケージのバージョンを固定 |
| `AudioCaptureApp/AudioCaptureApp.csproj` | 本体 ＋ win-x64 を参照。他 8 RID は `ExcludeAssets="all"` |
| `AudioCaptureApp/Models/TranscriptSegments.cs` | **新規。** `TranscriptSegment` / `SpeakerSegment` / `SpeakerAttributedSegment` |
| `AudioCaptureApp/Models/AppSettings.cs` | 設定 6 項目を追加 |
| `AudioCaptureApp/Services/SpeakerDiarizationService.cs` | **新規。** `SpeakerDiarizationOptions` / `SpeakerDiarizationException` / `SpeakerDiarizationService` |
| `AudioCaptureApp/Services/TranscriptDiarizationMerger.cs` | **新規。** `Merge` / `FormatSpeaker`（`internal static`） |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `FileTranscriptionProgress` 追加。`TranscribeFileAsync` に引数追加と分岐。Diarization 経路のメソッド群を追加 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `SpeakerDiarizationService` の生成・受け渡し・破棄。進捗表示のフェーズ対応 |
| `AudioCaptureApp.Tests/TranscriptDiarizationMergerTests.cs` | **新規。** 要求仕様 §23 の Case 1〜8 |
| `AudioCaptureApp.Tests/SpeakerDiarizationOptionsTests.cs` | **新規。** 設定値の clamp |
| `README.md` | モデルの入手・配置・設定・制約・ライセンス確認方法 |

## 7. 実装手順

### グループ A — パッケージ

- [x] **A1** `Directory.Packages.props` に `org.k2fsa.sherpa.onnx` と 9 RID ランタイムを 1.13.5 で固定
- [x] **A2** `AudioCaptureApp.csproj` に本体 ＋ win-x64 を参照し、他 8 RID に `ExcludeAssets="all"` を付ける
- [x] **A3** ビルドして `bin/Debug/net10.0-windows/runtimes/` が `win-x64` だけであることを確認する

### グループ B — Model

- [x] **B1** `Models/TranscriptSegments.cs` に 3 レコードを追加 (`Models/TranscriptSegments.cs`)
- [x] **B2** `AppSettings` に設定 6 項目を追加 (`Models/AppSettings.cs`)

### グループ C — Merger（先に書く。純粋関数なのでテストで固められる）

- [x] **C1** `TranscriptDiarizationMerger.Merge` を実装（重複長・同点は小さい ID・重複ゼロは不明）
- [x] **C2** `FormatSpeaker(int?)` を実装（0 始まり → `話者1` / `null` → `話者不明`）
- [x] **C3** §23 Case 1〜8 のテストを書く (`AudioCaptureApp.Tests/TranscriptDiarizationMergerTests.cs`)

### グループ D — Diarization サービス

- [x] **D1** `SpeakerDiarizationOptions`（clamp 付き）を実装
- [x] **D2** `SpeakerDiarizationException` を実装
- [x] **D3** `SpeakerDiarizationService`（モデル存在検査 → 生成 → レート検証 → `lock` 内で推論）を実装
- [x] **D4** clamp のテストを書く (`AudioCaptureApp.Tests/SpeakerDiarizationOptionsTests.cs`)

### グループ E — 文字起こし経路の結線

- [x] **E1** `FileTranscriptionProgress` を追加し、既存の進捗報告をフェーズ名付きに変える
- [x] **E2** `TranscribeFileAsync` に `SpeakerDiarizationService?` 引数を足し、`null` なら従来経路へ分岐
- [x] **E3** `DecodeToMono16k`（ファイル全体を 16kHz モノラルへ）を追加
- [x] **E4** `TranscribeFileWithDiarizationAsync`（Diarization → Whisper → Merge → 書き出し）を追加
- [x] **E5** `MainViewModel` で設定に応じて `SpeakerDiarizationService` を生成・受け渡し・`Dispose`

### グループ F — ドキュメント

- [x] **F1** README にモデルの入手・配置・設定・制約・ライセンス確認方法を追記
- [x] **F2** 取り込み済みとなる下書き `speaker-dialization.md`（リポジトリ直下・未追跡）を削除する

### グループ Z — 検証（必須・最後に置く）

- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

実機のモデルファイルを要求するテストは書かない（要求仕様 §24：CI にモデルが無くても
通常のユニットテストが落ちない構成にする）。したがって sherpa-onnx の推論そのものは
ユニットテストの対象外で、テストが守るのは **マージ規則と設定値の検証** である。

`TranscriptDiarizationMergerTests`

- **`Merge_完全一致する話者区間を割り当てる`** — Case 1。Transcript 0-5 / Speaker0 0-5 → 話者 0
- **`Merge_重複時間が長い話者を選ぶ`** — Case 2。Transcript 1-5 / S0 0-4 / S1 4-8 → 話者 0
- **`Merge_重複時間が同点なら小さい話者IDを選ぶ`** — Case 3。Transcript 3-7 / S0 0-5 / S1 5-10 → 話者 0（D5）
- **`Merge_話者区間が空なら話者不明になる`** — Case 4
- **`Merge_どの話者とも重ならないなら話者不明になる`** — Case 5（無音区間）。直前話者を引き継がないこと
- **`Merge_同一話者が再登場しても同じIDを保つ`** — Case 6。S0 / S1 / S0 の並び
- **`Merge_終了が開始より前の文字起こしセグメントを拒否する`** — Case 7（D7）
- **`Merge_終了が開始より前の話者区間を拒否する`** — Case 7（話者側）
- **`Merge_両方が空でも例外にならない`** — Case 8
- **`Merge_文字起こしが空なら空を返す`** — Case 8
- **`Merge_出力は入力の文字起こし順と件数を保つ`** — 行の欠落・並べ替えが起きないこと（D6 の裏返し）
- **`Merge_話者区間が順不同でも結果が変わらない`** — 入力順への依存が無いこと
- **`FormatSpeaker_0始まりの内部IDを1始まりの表示へ変換する`** — REQ-TRX-DIA-06
- **`FormatSpeaker_nullを話者不明として表示する`** — REQ-TRX-DIA-06

`SpeakerDiarizationOptionsTests`

- **`閾値が0以下なら既定値へ戻す`** — 0 以下だと sherpa-onnx の設定検証が失敗し、
  NULL ハンドル経由でプロセスが落ちるため、ここで必ず正の値にする
- **`閾値がNaNや無限大でも既定値へ戻す`**
- **`話者数が0以下ならnull（＝閾値を使う）にする`** — REQ-TRX-DIA-07
- **`話者数が正ならそのまま保持する`**
- **`スレッド数を1以上へ丸める`**

> **テストで守れない範囲:**
> - sherpa-onnx の推論そのもの（モデルファイルが要る）。話者が実際に正しく分かれるかは
>   実機確認でしか分からない。
> - モデル未配置時にプロセスが落ちないこと（`File.Exists` 検査が効いているか）は、
>   検査を外すとプロセスごと死ぬためユニットテストで再現できない。実機確認で見る。
> - `TranscribeFileAsync` の Diarization 経路全体（Whisper モデルと音声ファイルが要る）。

## 9. 未解決の質問

なし（適用範囲・パッケージ・出力書式・ブランチは着手前に確認済み）。

## 10. 前提

- `org.k2fsa.sherpa.onnx` 1.13.5 の C# API が調査時点（2026-08-22）の形のまま使える。
  実測した事実は ADR-0003「調査で分かった事実」F1〜F7 に記録した。
- 利用者が pyannote 系 segmentation モデルと話者埋め込みモデルを自分で配置できる。
- 対象音声は 16kHz モノラルへ正規化済みであり（REQ-TRX-05）、
  segmentation モデルの要求レートと一致する。一致しなければ実行時に検出して中止する。

---

## 実行結果 (2026-08-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 170 件成功 / 0 件失敗 / 0 件スキップ（うち本タスクで追加した 38 件）
- ビルド出力の `runtimes/`: `win-x64` のみに sherpa-onnx のネイティブが入ることを確認
  （`onnxruntime.dll` ＋ `sherpa-onnx-c-api.dll` = 約 21MB。未対策なら 9 RID 分 約 330MB）

### 計画からの逸脱

1. **テストメソッド名を英語（`対象_条件_期待結果`）にした。** 計画では日本語で列挙していたが、
   既存のテストがすべて英語のこの形式であり、そちらに合わせた。件数と検証内容は計画どおり。
2. **`DeletePartialOutput` を追加した（計画外）。** Z4 の読み合わせで、中止時の
   `TryDeleteFile(outputPath)` が Diarization 経路では **前回成功したときの
   `.transcript.txt` を巻き添えで削除する** ことに気づいたため。従来経路は開始時点で
   出力を `append: false` で開いて中身を捨てているので削除が正しいが、Diarization 経路は
   マージ後にしか開かないため中止時点では未作成である。あわせて最終書き出しからは
   キャンセル判定を外し、「全行書くか一行も書かないか」にした。REQ-TRX-FILE-07 に追記済み。
3. **`AppSettingsTests` の既存 3 テストに新しい設定項目のアサーションを足した（計画外）。**
   既定値・JSON 往復・キー欠落時の既定復帰は、既存の設定項目すべてに対して固定されている。
   新項目だけ穴が空くのは不整合であり、特に「キーが無い既存の settings.json で
   勝手に有効化されないこと」は固定する価値がある。テストメソッドは増えていない。

### 実機確認が残っている範囲

ユニットテストでは守れない（タスク票 §8 の「テストで守れない範囲」）。
モデル 2 種を配置したうえで、次を確認すること。

- 複数話者の音声で `話者1` / `話者2` が実際に分かれること
- 同一話者が再登場したときに同じ番号になること
- モデル未配置のまま有効化してもプロセスが落ちず、どのモデルが無いか分かるエラーが出ること
- 長い音声で `SpeakerDiarizationThreads` を増やすと速くなること
