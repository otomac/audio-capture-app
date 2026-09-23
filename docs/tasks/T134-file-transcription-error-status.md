# T134 — ファイル文字起こしの失敗理由がステータスバーに残らない

> **状態:** 完了 — 2026-09-22
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

失敗したときに何が起きたか（どのモデル・どのファイルで失敗したか）をステータスバーで分かるようにする。
ダイアログは REQ-TRX-FILE-12 で自動的に閉じるため、手がかりがステータスバーだけになる。

## 2. スコープ境界

**やること**
- 実機で再現させ、上書き順序が読みどおりか確認する（起票時の条件）
- `Error` イベントの内容を ViewModel が控え、失敗の 1 行に併記する

**やらないこと（重要）**
- **`Error` イベントの発火順序・Dispatcher の優先度は変えない**（同一優先度の FIFO は WPF の仕様。優先度を弄ると別の順序問題を招く）
- **ダイアログを失敗時に開いたままにはしない**（REQ-TRX-FILE-12 の自動クローズは維持。モデル読み込み失敗だけは T163 で別扱い）

## 3. 再現（2026-09-22、scratchpad `xamlcheck t134`。本体の `MainViewModel` と WPF Dispatcher をそのまま使用）

4KB のゼロ埋めファイル `broken.m4a` をドロップして「開始」。`StatusMessage` の変化を `PropertyChanged` で記録した。

1. `音声ファイルから文字起こし中...`
2. `文字起こしエラー: ファイル文字起こしエラー: 音声ファイルを開けませんでした (形式: .m4a): 指定された URL のバイト ストリーム タイプはサポートされていません。 (0xC00D36C4) Windows の N エディションでは Media Feature Pack が必要です。`
3. `文字起こしに失敗しました` ← **最終表示。2 の理由が消える**

読みどおり、`Error` → `BeginInvoke` で積んだ 2 が先に処理され、`await Task.Run` の継続（同じ Dispatcher・Normal 優先度）が 3 で上書きしている。

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 直し方 | `Error` の内容を `_lastTranscriptionError` に控え（ワーカースレッドで代入。string の代入は原子的）、失敗の 1 行を「文字起こしに失敗しました: <理由>」にする。理由が無ければ従来の文言 |
| D2 | 「文字起こしエラー: 」の接頭辞 | 併記では二重になるので、控えるのは `Error` の生のメッセージ |
| D3 | ライブ側 | 変えない（ライブの `Error` は録音中にステータスへ出るだけで、上書きする継続が無い） |

## 5. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-FILE-12 に失敗理由の併記と再現の事実を追記
- 変更なし: `02` / `03` / `04`

## 6. アーキテクチャへの影響

- ADR: **不要**。ViewModel 内の 1 フィールド。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `Error` ハンドラーで理由を控える |
| `AudioCaptureApp/ViewModels/MainViewModel.FileTranscription.cs` | 失敗の 1 行に理由を併記、実行前にクリア |
| `AudioCaptureApp.Tests/MainViewModelTests.cs` | `FileTranscriptionFailureMessageFor` |

## 8. テスト一覧

- **`FileTranscriptionFailureMessageFor_WithReason_AppendsReason`** / **`_WithoutReason_IsPlain`**

> **テストで守れない範囲:** Dispatcher の順序そのもの（上の再現で確認。修正後の再実行も §10）。

## 9. 未解決の質問

なし。

## 10. 修正後の再現（2026-09-22）

同じ手順で、最終表示が
`文字起こしに失敗しました: ファイル文字起こしエラー: 音声ファイルを開けませんでした (形式: .m4a): 指定された URL のバイト ストリーム タイプはサポートされていません。 (0xC00D36C4) Windows の N エディションでは Media Feature Pack が必要です。`
になった（上書きの順序は変わらないが、理由が残る）。

---

## 実行結果 (2026-09-22)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 317 件成功 / 0 件失敗 / 0 件スキップ（313 → 317、+4）
- 計画からの逸脱: なし
