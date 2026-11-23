# T208 — ファイル文字起こしのやり直しで、前の結果を上書きしない

> **状態:** 完了 — 2026-11-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

同じ音声でファイル文字起こしをやり直すと、前の `.transcript.txt` が確認なしに消える（出力先が固定で
`append: false` で開くため）。前の結果を残したまま、新しい結果を別名で書く。槇さんの要望 (2026-09-29)。

## 2. スコープ境界

**やること**
- 出力先が既にあれば、連番を付けた別名（`a.transcript (2).txt`、`(3)`…）に書く
- 実際に使った名前を完了時のステータスと直近の成果物（REQ-OPEN-01）に出す

**やらないこと（重要）**
- **確認ダイアログは足さない**（何も消さないので確認することが無い）
- **録音時のライブ文字起こし（`{録音}.txt`）は変えない**（録音ごとに新しい名前になるため衝突しない）
- **中止時の削除（REQ-TRX-FILE-07）は変えない**（消すのはその実行が作ったファイルだけ、のまま）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 連番か確認ダイアログか | **連番。** 台帳の既定案。利用者から異論なし（2026-11-23 に実施を依頼） |
| D2 | 番号の位置 | **`.transcript` と `.txt` の間**（`a.transcript (2).txt`）。`a (2).transcript.txt` にすると stem が変わり、メタデータ JSON（`BuildMetadataPath` は先頭の `.` より前を stem にする）が `a (2).json` という別物になって、REQ-TRX-FILE-19 の「既存の JSON を更新する」が効かなくなる |
| D3 | 番号の選び方 | 空いている最小の番号（2 から） |
| D4 | 使った名前の受け渡し | `FileTranscriptionResult.OutputPath` に載せる。ViewModel が名前を組み立て直すと、サービスが選んだ番号とずれるため |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-05 に連番の規則と理由を追加
- [x] `docs/spec/02_architecture.md` — 出力ファイルの表
- [x] `docs/spec/03_class_diagram.md` — `BuildAvailableTranscriptPath`、`FileTranscriptionResult.OutputPath`
- [x] `docs/spec/04_sequence_diagram.md` — ファイル文字起こしの書き出し先

## 5. アーキテクチャへの影響

- ADR: 不要（Services 内の名前の決め方と戻り値の項目の追加だけ）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `BuildAvailableTranscriptPath` を追加し `TranscribeFileAsync` で使う。`FileTranscriptionResult.OutputPath` |
| `AudioCaptureApp/ViewModels/FileTranscriptionViewModel.Run.cs` | 完了時のパスを `result.OutputPath` から取る |
| `AudioCaptureApp.Tests/RecordingMetadataFileTests.cs` | テスト 3 件 |

## 8. テスト一覧

- **`BuildAvailableTranscriptPath_Free_ReturnsPlainName`** — 空いていれば今までどおりの名前
- **`BuildAvailableTranscriptPath_Taken_ReturnsSmallestFreeNumber`** — 埋まっていれば空いている最小の番号
- **`BuildAvailableTranscriptPath_Numbered_SharesMetadataPathWithPlainName`** — 連番の名前でもメタデータ JSON は同じものを指す

> **テストで守れない範囲:** 実際のファイル文字起こしを 2 回続けて回し、2 つのファイルができることは実機で確かめていない
> （名前を決める関数をテストし、その戻り値がそのまま `StreamWriter` と完了表示に渡ることはコードで読んだ）。

---

## 実行結果 (2026-11-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 530 件成功 / 0 件失敗 / 0 件スキップ（3 件追加）
- 計画からの逸脱: なし
