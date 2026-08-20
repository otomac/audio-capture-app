# タスク台帳

**このファイルが唯一の進捗管理表である。** 様式は
[docs/harness/60-task-format.md](../harness/60-task-format.md) に従う。

- 状態: `[ ]` 未着手 / `[~]` 進行中 / `[x]` 完了 / `[-]` 取り下げ
- 同時に `[~]` にしてよいのは **原則 1 件**
- 行はセクション間を **移動** させる（複製しない）
- ID は再利用しない

---

## 進行中

（なし）

## 未着手
- [ ] **T114** リアルタイム文字起こしのテキストを表示するサブウィンドウを追加する（320x240・リサイズ可・9pt・録音停止で閉じない・プロセス終了で閉じる）
- [ ] **T115** 話者識別（Speaker Diarization）を sherpa-onnx で導入する → **要 ADR（新規外部ライブラリ採用 ＋ ADR-0001 の「Service 抽象化しない」方針との整合）／要パッケージ個別承認**
- [ ] **T109** `README.md` の「ビルド」節が存在しない `AudioCaptureApp.sln` を参照している（正: `AudioCaptureApp.slnx`）。T108 の作業中に発見

## 完了

- [x] **T113** 音声ファイル文字起こしの前にオプション指定モーダルダイアログを表示する（ファイル名表示・開始時刻 `hh:mm` 指定・進捗表示・キャンセル）。補助ウィンドウの追加にあたり [ADR-0002](../adr/0002-secondary-windows-share-mainviewmodel.md) を起票 (2026-08-20) → [詳細](./T113-file-transcription-options-dialog.md)
- [x] **T125** 短い物音が結合幅（既定 2.0 秒）内に複数あると 1 区間にまとまり、大半が無音のまま Whisper に渡る（例: 0.1 秒のクリック音 2 つが 1.5 秒の区間になる）。足切りを「結合後の区間幅」から「0.2 秒以上続く有声ランを含むか」へ変更した（有声密度案は実体のある短い発話を巻き添えで捨てるため不採用）。T112 のレビューで発見 (2026-08-20) → [詳細](./T125-voiced-region-density-cutoff.md)
- [x] **T126** `ProcessChunk` の `results` が `try` の内側で宣言されているため、Whisper がキャンセル例外を投げると**完了済み区間の行まで破棄される**。それらは `SegmentTranscribed` で画面には既に出ているので、`.txt` と画面が食い違う。T112 以前からある問題だが、1 チャンクが複数区間に分かれるようになり影響範囲が広がった。T112 のレビューで発見 (2026-08-20) → [詳細](./T126-processchunk-results-scope.md)
- [x] **T127** 録音停止の最悪レイテンシが T112 で悪化した。1 チャンクが最大 10 区間に分かれうるため、停止時の推論回数が最悪 3 回 → 30 回になり、`StopGraceTimeout`（30 秒）を超えると `DisposeProcessorSafely(workerExited: false)` の意図的リーク経路に入る（T117 が直した症状の再来）。`ProcessChunk` の区間ループは `token` しか見ておらず、猶予期間中はまだキャンセルされていない。`_isRunning` を見る案は排出処理（drain）を握り潰すので不可 — `interruptible` 引数で経路を分ける必要がある。T112 のレビューで発見 (2026-08-20) → [詳細](./T127-stop-latency-region-loop.md)
- [x] **T112** 無音区間を Whisper に渡さないようにカットし、無音部でのハルシネーション（「ご視聴ありがとうございました」等）を抑止する (2026-08-20) → [詳細](./T112-silence-cut-before-whisper.md)
- [x] **T124** ブランチ運用（develop 最新化 → feature ブランチ作成 → ゲート全緑後に commit/push/PR）を規範化し、保護ブランチ上のソース編集をフックで deny する (2026-08-18) → [詳細](./T124-branch-workflow-enforcement.md)
- [x] **T123** GPU が使えない環境でも `GpuAvailable` が真になり「GPU 実行」と表示される（GPU 版ランタイム DLL はデバイスが無くても読み込めるため。ネイティブログの `backends` 数と重みの配置先で判定するよう変更） (2026-08-18) → [詳細](./T123-gpu-availability-false-positive.md)
- [x] **T122** `TranscriptionService.LoadModel` がモデル読み込みの失敗を返せない（`WhisperFactory.FromPath` はネイティブ側の読み込み失敗を例外にせずファクトリを返すため、壊れたモデルでも「読み込み完了」と表示されていた。`CreateBuilder()` で失敗を確定させる） (2026-08-18) → [詳細](./T122-loadmodel-failure-not-reported.md)
- [x] **T119** 「文字起こしにGPUを使用する」を OFF にしても GPU で動き続ける（`RuntimeLibraryOrder` による CPU 限定の再読み込みは、Whisper.net がネイティブランタイムをプロセス単位で 1 度しかロードしないため空振りしていた。`WhisperFactoryOptions.UseGpu` へ変更） (2026-08-18) → [詳細](./T119-gpu-toggle-ineffective.md)
- [x] **T121** 録音中・停止処理中・ファイル文字起こし中は「保存先を開く」を無効化する（直近の成果物が現在の作業と食い違い、表示上も違和感がある。T111 の追補） (2026-08-18) → [詳細](./T121-disable-open-folder-while-busy.md)
- [x] **T111** 文字起こし完了時に保存先フォルダを開けるようにする（要件 3） (2026-08-16) → [詳細](./T111-open-result-folder.md)
- [x] **T118** マイクのモニタリング開始失敗でアプリが起動できない（`E_HANDLE` が `MainViewModel` コンストラクターから漏れる） (2026-08-16) → [詳細](./T118-mic-monitor-startup-crash.md)
- [x] **T120** 供給が止まったバッファを時間経過で書き出す（ミュート中に書き出しが無制限に遅延し、`.txt` の行順が時刻順にならない） (2026-08-16) → [詳細](./T120-stale-buffer-flush.md)
- [x] **T117** 録音停止時に長時間ブロックし `Cannot dispose while processing` で異常終了する (2026-08-16) → [詳細](./T117-stop-session-crash.md)
- [x] **T116** ライブ文字起こしの記録時刻が実時刻とずれる（無音／ミュート区間で時計が進まない）＋短い・小音量の発話が丸ごと捨てられる (2026-08-16) → [詳細](./T116-live-transcript-timestamp.md)
- [x] **T110** スピーカー（ループバック）のレベルメーターを録音停止中でも動作させる (2026-08-16) → [詳細](./T110-speaker-meter-always-on.md)

- [x] **T108** 実行基盤を .NET 8 から .NET 10 へ引き上げる（アプリ／テスト／CI） (2026-08-15) → [詳細](./T108-net10-upgrade.md)
- [x] **T107** `SettingsService` のフィールド初期化をコンストラクターに集約（宣言時とコンストラクターへの分散を解消） (2026-08-15)

- [x] **T106** `.claude/settings.local.json` を git 管理から外す (2026-08-14) → [詳細](./T106-untrack-local-settings.md)
- [x] **T105** 軽微な静的解析指摘の解消（CA1822 / CA1825） (2026-08-14) → [詳細](./T105-minor-analyzer-findings.md)
- [x] **T104** 非同期の是正 — 同期呼び出しの解消と `CancellationToken` の伝播（CA1849 / CA2016） (2026-08-14) → [詳細](./T104-async-discipline.md)
- [x] **T103** `catch (Exception)` の局所化と理由付与（CA1031） (2026-08-14) → [詳細](./T103-narrow-exception-handling.md)
- [x] **T102** カルチャー・文字列比較の明示（CA1305 / CA1307） (2026-08-14) → [詳細](./T102-culture-and-string-comparison.md)
- [x] **T101** Dispose パターンの是正（CA1063 / CA1816 / CA1001 / CA2213 / CA2000） (2026-08-14) → [詳細](./T101-dispose-pattern-cleanup.md)
- [x] **T100** 開発ハーネスの整備（`docs/harness/` の規範・強制フック・ビルドゲート） (2026-08-14) → [詳細](./T100-dev-harness.md)
