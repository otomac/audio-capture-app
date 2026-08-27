# T156 — `MainViewModel` を `partial` で機能単位のファイルに割る

> **状態:** 完了 — 2026-08-27
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **根拠:** [ADR-0005](../adr/0005-mainviewmodel-split.md)（承認済み・2026-08-27）

## 1. 目的

`MainViewModel.cs` は 1,396 行あり、分割の閾値 1,500 行（[20-architecture-standards.md §6](../harness/20-architecture-standards.md)）
まで約 100 行しかない。ADR-0005 の決定に従い、**クラスは 1 つのまま `partial` でファイルだけを
機能単位に割る**。1 ファイルあたりの行数を下げ、読む範囲を絞れるようにする。

## 2. スコープ境界

**やること**

- `MainViewModel` を `partial` のまま複数ファイルへ機械的に移す
- 移動に伴って必要な `using` の整理

**やらないこと（重要・これが本タスクの肝）**

- **挙動を 1 ミリも変えない。** 本タスクは**純粋な移動**であり、リファクタリングではない。
- **`public` / `internal` な形を変えない。** メンバー名・シグネチャ・アクセス修飾子はそのまま。
  したがって**テストは 1 行も変更しない**（変更が要るなら、それは移動ではなく改変である）。
- **メンバーの中身を書き換えない。** 「ついでに直す」を禁止する。気づいた問題は別タスクへ起票する。
- **クラスを増やさない。** `ViewModels/` に `MainViewModel` 以外のクラスを置かない（ADR-0005 の規則 3）。
- **`docs/spec/01_requirements.md` は変えない。** 要件は変わらない。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 分割の方式 | **`partial class`。** クラスもインスタンスも 1 つのまま（ADR-0005 案 D） |
| D2 | ファイルの割り方 | **機能単位**（§4）。1 ファイル 500 行を目安とする（ADR-0005 の規則 2） |
| D3 | 移動の粒度 | **メンバー単位でそのまま移す。** 中身は触らない。差分は「消えた行」と「同じ内容で現れた行」だけになるはず |
| D4 | ソースジェネレーター | `[ObservableProperty]` / `[RelayCommand]` は `partial class` をまたいでも動く（既に `public partial class MainViewModel`）。**動くことをビルドで確認する**（G1） |
| D5 | 検証の主眼 | **挙動が変わっていないこと。** テスト無変更で G3 が通ることをもって示す。加えて手動で主要導線を確認する |

## 4. ファイルの割り方（案）

| ファイル | 持つもの | 概算 |
|---|---|---|
| `MainViewModel.cs` | コンストラクター、Service の保持、`IsNotBusy` などの共有状態、`StatusMessage`、`LastResultPath`、`Dispose` | 250 |
| `MainViewModel.Devices.cs` | デバイス一覧・選択・モニタリング・ミュート・レベルメーター | 300 |
| `MainViewModel.Recording.cs` | 録音の開始／停止、経過時間、終了時の確認（`CloseConfirmationMessage` / `ShutdownAsync`） | 300 |
| `MainViewModel.Transcription.cs` | Whisper モデルの読み込み、GPU 切り替え、言語の選択肢、話者識別の状態 | 250 |
| `MainViewModel.FileTranscription.cs` | ファイル文字起こしの進行管理、開始時刻の推定、ドラッグ＆ドロップ | 350 |
| `MainViewModel.LiveTranscript.cs` | ライブ表示の行の蓄積とフラッシュ | 150 |

概算の合計が現在の 1,396 行と一致しないのは、関心が行をまたいで交錯しているためである。
**実際の境界は実装時に確定する。** 上の表は目安であり、
「1 ファイルが 500 行を超えないこと」だけが守るべき条件である。

## 5. 仕様書への影響

- [x] `docs/spec/02_architecture.md` — 冒頭の方針、レイヤー図のノード、ViewModel 層の責務にファイル一覧の表を追加
- [x] `docs/spec/03_class_diagram.md` — **変更なし。** クラス図はクラスを描くものであり、
      クラスは 1 つのままなので変わらない。ファイル構成に触れている記述も無かった（確認済み）

## 6. アーキテクチャへの影響

- ADR: **不要。** [ADR-0005](../adr/0005-mainviewmodel-split.md) で承認済みの決定を実施するタスクである。
- 規範側（`CLAUDE.md` / `20-architecture-standards.md`）は **T155 で更新済み**。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 大半のメンバーを他ファイルへ移す |
| `AudioCaptureApp/ViewModels/MainViewModel.Devices.cs`（新規） | §4 のとおり |
| `AudioCaptureApp/ViewModels/MainViewModel.Recording.cs`（新規） | 同上 |
| `AudioCaptureApp/ViewModels/MainViewModel.Transcription.cs`（新規） | 同上 |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs`（新規） | 同上 |
| `AudioCaptureApp/ViewModels/MainViewModel.LiveTranscript.cs`（新規） | 同上 |
| `docs/spec/02_architecture.md` | ViewModel 層の記述 |

**`AudioCaptureApp.Tests/` は変更しない。** 変更が必要になったら、それは移動を超えた改変である。

## 8. 実装手順

- [x] **A1** メンバーを 6 グループへ割り当てた（17 個の連続した行範囲。境界はすべて空行であることを確認）
- [x] **A2** **行範囲をそのまま抽出して連結する方式**で一括生成した（理由は「計画からの逸脱」1）
- [x] **A3** 各ファイルの `using` を、本文に型名が現れるものだけへ絞った
- [x] **A4** `docs/spec/02_architecture.md` を更新した
- [x] **Z1** `dotnet build` — 警告 0 件 / エラー 0 件
- [x] **Z2** `dotnet format --verify-no-changes` — 差分なし（終了コード 0）
- [x] **Z3** `dotnet test` — 241 件成功 / 0 件失敗（**分割前と同数**。テストは 1 行も変更していない）
- [x] **Z4** 原本と新 6 ファイルの本文を**行の多重集合として比較**した。差は**空行 5 行のみ**
- [~] **Z5** 起動と正常終了は自動で確認した（ウィンドウが出て `exit=0` で閉じる）。
      **録音・ミュート・文字起こし・保存先を開く の対話操作は未実施**（「実行結果」に記載）

## 9. テスト一覧

**追加・変更なし。** 本タスクは純粋な移動であり、テストが 1 行も変わらずに全件通ることが
「挙動を変えていない」ことの証明になる。**テストを直したくなったら、その時点で移動を逸脱している。**

## 10. 未解決の質問

なし（D1〜D5 で確定。方式は ADR-0005 で承認済み）。

## 11. 前提

- `MainViewModel` は既に `public partial class` である（`MainViewModel.cs:13`）。
  CommunityToolkit.Mvvm のソースジェネレーターは同じ `partial class` に対して生成するため、
  ファイルが分かれても動作は変わらない。これは D4 として G1 で確認する。
- `.editorconfig` は `[*.cs]` に `end_of_line = crlf` を指定している。新規ファイルも CRLF で作る
  （`.gitattributes` が `*.cs text eol=crlf` を強制するため、通常は自動で揃う）。

---

## 実行結果 (2026-08-27)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : **241 件成功 / 0 件失敗 / 0 件スキップ**（分割前と同数。テストは無変更）

### 分割後の行数

| ファイル | 行数 | §4 の目安 |
|---|---|---|
| `MainViewModel.cs` | 316 | 250 |
| `MainViewModel.Devices.cs` | 156 | 300 |
| `MainViewModel.Recording.cs` | 191 | 300 |
| `MainViewModel.Transcription.cs` | 282 | 250 |
| `MainViewModel.FileTranscription.cs` | 382 | 350 |
| `MainViewModel.LiveTranscript.cs` | 116 | 150 |
| **合計** | **1,443** | — |

分割前は 1 ファイル 1,397 行。**最大でも 382 行**になり、条件（1 ファイル 500 行以内）を満たす。

### 「移動だけ」であることの検証（Z4）

`git show HEAD:...MainViewModel.cs` の本文（15〜1396 行 ＝ 1,382 行）と、新しい 6 ファイルの
本文を**行の多重集合として突き合わせた**。結果は **欠落 5 行・追加 0 行**で、
**5 行はすべて空行**である（各新規ファイルの先頭から落とした 1 行 × 5）。
**非空行は 1 行も増減していない。**

### 計画からの逸脱

1. **A2 の進め方を変えた。** 「1 ファイルずつ手で移してその都度ビルド」ではなく、
   **行範囲を抽出して連結するスクリプト**で一括生成した。手で書き写すと転記ミスが
   混入しうるためで、Z4 の検証もこの方式だから機械的に行えた。
2. **各新規ファイルに 2 行の説明コメントを足した**（どの機能を担当するか、ADR-0005 への参照）。
   純粋な移動からの逸脱だが、ファイルを開いた人が役割を判断できないと分割の意味が薄いため。
   `///` ではなく `//` にしたのは、`partial` クラスの各パートに XML ドキュメントコメントを
   置くと重複の警告になりうるためである。
3. **A3 の `using` 整理は機械判定である。** 本文に型名が現れるかで判定したため、
   コメント中にだけ現れる型があると、その `using` は残る。過剰に残る方向にしか外れず、
   ビルドは警告 0 件で通っている。
4. **Z5 は途中までである。** 起動と正常終了（`exit=0`）は自動で確認したが、
   録音・ミュート・文字起こし・保存先を開く の対話操作は行っていない。

### 引き継ぎ事項（要判断）

**[ADR-0005](../adr/0005-mainviewmodel-split.md) の再評価契機①「全ファイル合計 1,500 行」に、
分割そのものが近づけてしまった。** `using` / `namespace` / `class` の定型が 6 セットに増えたため、
合計は 1,397 → **1,443 行**（閾値まで 57 行）で、分割前の 1,396 行より閾値に近い。

次に触る [T154](./T154-ui-refresh.md) が概算 +10 行なので約 1,453 行になる。
**契機①の数え方（定型行を含む合計でよいか、コード行で数えるべきか）は近いうちに判断が要る。**
勝手に閾値を動かすのは ADR の書き換えにあたるため、本タスクでは触っていない。
