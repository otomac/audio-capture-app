# T104 — 非同期の是正（同期呼び出しの解消と `CancellationToken` の伝播）

> **状態:** 完了 — 2026-08-14
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

`async` メソッド内の同期ブロッキング呼び出し（CA1849）と `CancellationToken` の非伝播（CA2016）を
解消し、`.editorconfig` の負債行を削除する。

## 2. 着手時の実測（重要）

ハーネス導入時点の実測では CA1849 が 5 件（`TranscribeFileAsync` の `writer.Dispose()` ×3 /
`processor.Dispose()` / `reader.Dispose()`）あったが、**[T101](./T101-dispose-pattern-cleanup.md) で
`using` 宣言へ移した結果、CA1849 の報告は 0 件になった**（CA1849 は明示的な `.Dispose()` 呼び出しを
検出するもので、`using` 宣言は検出しないため）。

緩和を外したビルドで残るのは **CA2016 の 1 箇所のみ**である。

```
TranscriptionService.cs(358,15): error CA2016:
  'ct' パラメーターを 'FlushAsync' メソッドに転送するか、または 'CancellationToken.None' を…
```

ただし **同期 `using` は `async` メソッド内で `Dispose()` を同期実行しており、
アナライザーが黙っただけで実体は残っている。** T101 の決定 D3 で
「`await using` 化は T104」と明記して繰り延べた分であるため、本タスクで完了させる。

## 3. スコープ境界

**やること**
- `TranscribeFileCoreAsync` の 3 つの `using` 宣言を `await using` にする（T101 からの繰り延べ分）
- `ProcessFileChunkAsync` の `writer.FlushAsync()` に `ct` を伝播する（CA2016）
- `.editorconfig` から T104 の負債 2 行を削除する

**やらないこと（重要）**
- **`TranscriptionService.ProcessChunk` の `GetAwaiter().GetResult()`** — 同期メソッド
  （専用スレッド `WhisperTranscription` 上）であり CA1849 の対象外。非同期化はスレッドモデルの
  変更にあたり、ADR が必要になる。本タスクの範囲外
- **`writer.WriteLineAsync(line)`** — `string` 引数のオーバーロードに `CancellationToken` 版が
  無く、CA2016 も報告していない。`ReadOnlyMemory<char>` 版への変更はしない
- **`ConfigureAwait` の付け外し** — CA2007 は恒久緩和済み。既存の `ConfigureAwait(false)` は
  Service 層内部の記述であり触らない
- **T105 の対象ルール**

## 4. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | `FlushAsync` に何を渡すか | **`ct` を伝播する。** 20 秒チャンクごとのフラッシュであり、中止操作に追従できたほうがよい。中止時の部分ファイルは `TranscribeFileAsync` 側が削除する（T101 の D3）ため、途中でフラッシュが切れても実害がない |
| D2 | `await using` への変更が破棄順序を変えないか | **変えない。** `await using` 宣言も同じスコープ末尾で逆順に破棄され、例外は破棄完了後に伝播する。`TryDeleteFile` がファイルハンドル解放後に走る保証（T101 の D3）は維持される |

## 5. 仕様書への影響

**仕様非影響。** 理由: 変更対象は `private` メソッドの内部実装のみで、公開シグネチャも
外から見た振る舞いも変わらない（[50-spec-standards.md §2](../harness/50-spec-standards.md#2-章立てと更新対象の判定)
「更新が不要な変更」）。

## 6. アーキテクチャへの影響

- ADR: **不要**。スレッドモデルは変わらない（`ProcessChunk` の同期実行を維持するのが §3「やらないこと」）。

## 7. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TranscribeFileCoreAsync` の 3 つの `using` → `await using`、`ProcessFileChunkAsync` の `FlushAsync(ct)` |
| `.editorconfig` | T104 の負債 2 行（CA1849 / CA2016）を削除 |

## 8. 実装手順

### グループ A — 実装
- [x] **A1** `TranscribeFileCoreAsync` の `reader` / `writer` / `processor` を `await using` に
- [x] **A2** `ProcessFileChunkAsync` の `writer.FlushAsync()` に `ct` を渡す

### グループ B — 緩和の削除
- [x] **B1** `.editorconfig` の T104 負債 2 行を削除
- [x] **B2** [40-quality-gates.md §5](../harness/40-quality-gates.md#5-技術的負債期限付き緩和) の負債表から T104 行を削除

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功

## 9. テスト一覧

**追加なし。** 対象の `TranscribeFileCoreAsync` / `ProcessFileChunkAsync` は Whisper モデル実体と
音声ファイルを要求するため、ユニットテストの対象外
（[40-quality-gates.md §4](../harness/40-quality-gates.md#4-既知の穴)「実機依存の挙動」）。

> **テストで守れない範囲:** `await using` 化後の破棄順序と、中止時の `.transcript.txt` 削除。
> D2 の根拠（`await using` のスコープ規則）と手動確認で担保する。

## 10. 未解決の質問

なし。

## 11. 前提

- `WhisperProcessor` / `AudioFileReader` / `StreamWriter` はいずれも `IAsyncDisposable` を実装する
  （前者はビルド実測時の CA1849 メッセージが `WhisperProcessor.DisposeAsync()` を示していた事実、
  後 2 者は `Stream` / `TextWriter` の BCL 実装による）。

---

## 実行結果 (2026-08-14)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 26 件成功 / 0 件失敗 / 0 件スキップ
- 計画からの逸脱: なし（§2 のとおり、CA1849 の報告件数が着手時点で 0 件だった点は
  計画に取り込み済み。`await using` 化は T101 からの繰り延べ分として実施した）
