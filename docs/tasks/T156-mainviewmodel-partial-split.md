# T156 — `MainViewModel` を `partial` で機能単位のファイルに割る

> **状態:** 未着手
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

- [ ] `docs/spec/02_architecture.md` — ViewModel 層の記述とレイヤー図を `MainViewModel*.cs`（`partial`）に合わせる
- [ ] `docs/spec/03_class_diagram.md` — **クラス図は変わらない**（クラスは 1 つのまま）。
      ファイル構成に触れている記述があれば直す

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

- [ ] **A1** 現在の `MainViewModel.cs` のメンバーを §4 の 6 グループへ割り当てる（表を確定させる）
- [ ] **A2** ファイルを 1 つずつ作り、メンバーを移す。**1 ファイル移すごとにビルドを通す**
- [ ] **A3** 各ファイルの `using` を必要なものだけに整理する
- [ ] **A4** `docs/spec/02_architecture.md` を更新する
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功（**件数が分割前と同じであること**）
- [ ] **Z4** `git diff` を読み、**移動以外の変更が 1 行も無いこと**を確認する
- [ ] **Z5** 起動して主要導線を手動確認（録音の開始／停止、ミュート、ライブ文字起こし、ファイル文字起こし、保存先を開く）

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

## 実行結果

（未実施）
