# T160 — 中止の注記を「押した時点のフェーズ」で出し分ける

> **状態:** 完了 — 2026-08-28
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

T157 で入れた中止の注記が、Whisper の「処理中」フェーズで押しても
「話者識別が終わるまでお待ちください」と出る。事実に反するので、判定を
「話者識別が有効か」から「押した時点のフェーズ」へ変える。

## 2. スコープ境界

**やること**
- 進捗が運ぶ `Phase`（REQ-TRX-FILE-06）を `MainViewModel` 側で覚え、注記の出し分けに使う。
- `FileTranscriptionCancelNotice` を計算プロパティから、押した時点で確定する
  `[ObservableProperty]` へ変える。
- フェーズ名の定数を `TranscriptionService` から `internal` で見えるようにする。
- REQ-TRX-FILE-07 の文言の規則を差し替える。

**やらないこと（重要）**
- **中止がいつ効くかという判定ロジックは 1 行も変えない**（REQ-TRX-DIA-12 を維持）。
- **フェーズ名の文字列そのものを変えない**（"話者識別中" / "処理中" は REQ-TRX-FILE-06 の表示）。
- **`TranscriptionService` の処理の流れ・進捗の報告回数・報告する値に触らない。**
  変えるのは 2 つの `const` の**可視性だけ**である。
- **ボタンの無効化（T157 の D5）と、注記を進捗行とは別に置く構造（T157 の D1）は維持する。**
- **`MainWindow.xaml` / `SettingsWindow.xaml` に触らない**（T158 で確定済み）。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 何で出し分けるか | **押した時点のフェーズ。** `IProgress` が運ぶ `Phase` を `MainViewModel` が覚えておく。**T157 の D4（`_speakerDiarizationService != null` で決める）を破棄する** |
| D2 | 注記の持ち方 | **`[ObservableProperty]` にして、押した時点で値を確定させる。** T157 では「値が変わらないから変更通知は要らない」としていたが、フェーズで変わる以上その前提が崩れた |
| D3 | 押した後にフェーズが進んだら | **書き換えない。** 話者識別中に押せば、その完了直後の `ct.ThrowIfCancellationRequested()` で中止が効き、Whisper へは入らない（`TranscribeFileWithDiarizationAsync`）。したがって注記が古くなる経路が無い |
| D4 | フェーズ名の比較方法 | **`TranscriptionService` の `const` を `internal` にして参照する。** ViewModel 側に "話者識別中" を書き写すと、表示名を変えたときに黙って壊れる |
| D5 | 準備中（最初の進捗が届く前）の扱い | **「処理の切れ目までお待ちください」側。** この間に走るのは `DecodeToMono16k` で、`ct` を渡してあるため素早く止まる |
| D6 | 判定の置き場 | **`internal static bool IsDiarizationPhase(string)`。** `Progress` のラムダの中に書くとテストできない |
| D7 | 文言そのもの | **変えない。** T157 の 2 つをそのまま使う（誤っていたのは選び方であって文言ではない） |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-07 の「文言の出し分け」の規則を差し替え。
      「話者ダイアライゼーションが有効かで決めてはならない」理由と、フェーズごとに待ち時間が
      違うことを明記。対象欄に `IsDiarizationPhase` を追加
- [ ] `docs/spec/02_architecture.md` — 影響なし
- [ ] `docs/spec/03_class_diagram.md` — 影響なし。`+string FileTranscriptionCancelNotice` は
      計算プロパティから `[ObservableProperty]` に変わるだけで、公開面は同じ文字列プロパティのまま
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし。§6 の注記（「注記は別の行なので消えない」）は
      引き続き正しい

## 5. アーキテクチャへの影響

- ADR: **不要。** `const` 2 つの可視性が `private` → `internal` になるだけで、依存方向は
  ViewModel → Service のまま変わらない（逆向きの参照は生じない）。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TranscribePhase` / `DiarizePhase` を `private const` → `internal const` にする（**それだけ**） |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | `_isDiarizingFile` を追加し進捗ハンドラーで更新。`IsDiarizationPhase` を追加。`FileTranscriptionCancelNotice` を `[ObservableProperty]` へ。`FileTranscriptionCancelNoticeFor` の引数名を `waitingForDiarization` に。`CancelFileTranscription` で注記を確定。リセット箇所を 2 つとも更新 |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | 既存 2 件をフェーズ基準の名前へ改め、`IsDiarizationPhase` のテスト 2 件を追加 |
| `docs/spec/01_requirements.md` | §4 のとおり（済） |

## 7. 実装手順

### グループ A — Service
- [ ] **A1** `TranscribePhase` / `DiarizePhase` を `internal const` にする (`TranscriptionService.cs`)

### グループ B — ViewModel
- [ ] **B1** `IsDiarizationPhase(string)` を `internal static` で追加する
- [ ] **B2** `_isDiarizingFile` フィールドを追加し、進捗ハンドラーの先頭で更新する
- [ ] **B3** `FileTranscriptionCancelNotice` を `[ObservableProperty]` へ変える（計算プロパティを削除）
- [ ] **B4** `FileTranscriptionCancelNoticeFor` の引数名を `waitingForDiarization` にし、docコメントを直す
- [ ] **B5** `CancelFileTranscription` で `FileTranscriptionCancelNotice` を確定させる
- [ ] **B6** `RequestFileTranscription` / `RunFileTranscriptionAsync` で `_isDiarizingFile` と注記を戻す

### グループ C — テスト
- [ ] **C1** 既存 2 件の名前と意図をフェーズ基準へ改める
- [ ] **C2** `IsDiarizationPhase` のテストを 2 件足す

### グループ Z — 検証（必須・最後に置く）
- [ ] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [ ] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [ ] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [ ] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`IsDiarizationPhase_DiarizePhase_IsTrue`** — 話者識別のフェーズ名を話者識別と見なすこと。
  `TranscriptionService` の定数と突き合わせるので、表示名を変えたら落ちる。
- **`IsDiarizationPhase_TranscribePhase_IsFalse`** — Whisper のフェーズ名を話者識別と見なさないこと
  （**本タスクの不具合そのもの**を突く）。
- **`FileTranscriptionCancelNoticeFor_WaitingForDiarization_MentionsSpeakerIdentification`** —
  話者識別を待っているときの注記が「話者識別」に触れること。
- **`FileTranscriptionCancelNoticeFor_NotWaitingForDiarization_DoesNotMentionSpeakerIdentification`** —
  それ以外のときに「話者識別」と言わないこと。

> **テストで守れない範囲:** 「Whisper のフェーズで押したら正しい方が出る」という結合は、
> `Progress` のハンドラーが実際に呼ばれる必要があり、実音声と Whisper モデルを要求するので
> 単体テストにしない。守れているのは①フェーズ名の対応付け（`IsDiarizationPhase`）と
> ②対応付けから文言への写像（`FileTranscriptionCancelNoticeFor`）の 2 段で、
> **その 2 つを繋ぐ 1 行（進捗ハンドラー内の代入）だけが機械で守られていない。**
> ここは実機で、話者識別有効の音声を流して「処理中」に入ってから中止して確認する。

## 9. 未解決の質問

なし。

## 10. 前提

- `Progress<T>` は UI スレッドで生成されるため、ハンドラーも UI スレッドで走る
  （`_isDiarizingFile` に同期が要らない根拠）。`CancelFileTranscription` も UI スレッド。
- 話者識別中に中止を押した場合、`Diarize` の直後の `ct.ThrowIfCancellationRequested()` で
  必ず抜ける（D3 が成り立つ根拠。`TranscribeFileWithDiarizationAsync` の ①→② の境目）。

---

## 実行結果 (2026-08-28)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 244 件成功 / 0 件失敗 / 0 件スキップ（242 → 244。本タスクで 2 件追加、2 件改名）
- 計画からの逸脱: なし。
- **機械で確認できていない点:** 進捗ハンドラー内の `_isDiarizingFile = IsDiarizationPhase(v.Phase);`
  の 1 行だけがテストで守られていない（§8 のとおり）。実機で、話者識別有効の音声を
  「処理中」まで進めてから中止して文言を確認する必要がある。
