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

- [ ] **T137** 話者の割り当て単位が Whisper の**セグメント**なので、1 セグメントの途中で話者が変わると全体が重複時間の長い方へ寄る。実測（T115 のレビュー）で、現行設定の「混在」（同一 ID が別人に跨る）**2 件はすべてこれが原因**と特定した — `話者1` が相槌 「えー、どれまでですか?」「へぇー」を巻き込み、`話者8` が「（笑って）いまなんと…」を巻き込んでいた。**話者の区切り自体は正しく検出できており、テキストの切れ目が追従していない。**埋め込みモデル 4 種 × 閾値 4 通り（計 16 通り）を実測しても混在は 0 にならず、設定では直らない。Whisper.net 1.9.0 に `WithTokenTimestamps()` / `WhisperToken.Start`・`End`・`Text`（10ms 単位）と、高精度整列の `WhisperFactoryOptions.UseDtwTimeStamps` ＋ `HeadsPreset.Small`（使用中の `ggml-small.bin` に対応）が**実在することを確認済み**。トークン境界で `TranscriptSegment` を割ってから Merger へ渡す。**Whisper への入力は変えない**（`TranscriptDiarizationMerger` は粒度非依存に作ってあるので無変更）。仕様 §13 の制約記述と REQ-TRX-DIA-05 の改訂を伴う。T115 のレビューで発見
- [ ] **T138** 同一人物の話者 ID が断片化する（A が `話者1`/`話者2`/`話者6`/`話者29`/`話者30` に割れた）。T115 のレビューで埋め込みモデル 4 種（campplus_zh_en / eres2netv2_zh-cn / nemo_titanet_large / wespeaker_CAM++）× 閾値 0.4〜0.7 を実測したが、**断片化は最良でも 3 ID（理想 1）で頭打ち**。既定値を動かす根拠としては弱い。**閾値を 0.8 以上に上げるのは有害**と判明している — 同一人物の断片がまとまるのではなく相手の話者に飲み込まれ、正しく取れていた区別（15:08:51「間違いなそう」）を失う。`KnownSpeakerCount=2` も同じ理由で逆効果。`MinDurationOff` の 0.5→0.2→0.1 は全 58 行のラベルが完全同一で**無関係**と確認済み。**2 分 42 秒の音声 1 本での測定なので、複数の音声で再測してから既定値の可否を判断する**（1 本での最適化はしない）。T115 のレビューで発見
- [ ] **T139** 有声区間の分割位置（`SplitVoicedRegions`）は無音の長さだけで決めているが、そこへ話者境界の情報を足せば、**区間数を増やさずより良い位置で切れる**。「すでに切ってよい無音の中で、話者が交代している点を選ぶ」だけなので、音声を余計に細かくせず単語の途中も切らない。**Diarization の結果で音声を切り分けてから Whisper に渡す構成（要求仕様 §4 と ADR-0003 争点 2 が禁止）とは別物**であることに注意 — 前者は境界誤りがテキストそのものを壊し、T112 で潰した短断片ハルシネーションも再発させる。なお相槌のような「無音を挟まない話者交代」には効かないため **T137 の代わりにはならず補助にとどまる**。**T137 の効果を見てから要否を判断する**（先に着手しない）。T115 のレビューで発見
- [ ] **T134** ファイル文字起こしが失敗したとき、`Error` イベントで通知した**失敗理由がステータスバーに残らない**。ワーカーが `Error?.Invoke("ファイル文字起こしエラー: ...")` で `Dispatcher.BeginInvoke` を積んだ直後に、`await Task.Run(...)` の継続が同じ Dispatcher へNormal 優先度で積まれ、`StatusMessage = "文字起こしに失敗しました"` が上書きすると読める（同一優先度の FIFO）。ダイアログは REQ-TRX-FILE-12 で自動的に閉じるため、手がかりがステータスバーだけになり原因が分からない。T115 以前からある挙動だが、REQ-TRX-DIA-11 が「どのモデルで失敗したか分かるメッセージ」を要求したことで実害が顕在化した。**まず実機で再現させ、上書き順序が読みどおりか確認してから修正する**（コードリーディングのみの推測段階）。T115 のレビューで発見
- [ ] **T135** 話者ダイアライゼーション有効時、`SegmentTranscribed` が**書き出しの最後に全行まとめてバースト発火**する（`WriteAttributedSegmentsAsync` のループ）。1 時間の会議なら千行規模の `Dispatcher.BeginInvoke` が連続で積まれる一方、表示上限は 100 行（`MaxLiveTranscriptLines`）なので大半は積まれた直後に捨てられる。従来のストリーミング経路は時間的に分散していたため、これは T115 で新規に生じた負荷である。正しさの問題ではないが書き出し完了時に UI がもたつく可能性がある。**まず実機で体感・実測してから、対処が要るかを判断する**（間引き／まとめて 1 回の発火などが候補）。T115 のレビューで発見

## 完了

- [x] **T136** `docs/spec/01_requirements.md` の REQ-TRX-DIA-11 が実装箇所を`TranscriptionService.TranscribeFileWithDiarizationAsync` としているが、実際に `Error` イベントを発火しているのは例外を捕捉する側の `TranscribeFileAsync` である（`TranscribeFileWithDiarizationAsync` は `SpeakerDiarizationException` を投げるだけ）。T115 で書いた記述の誤り。T115 のレビューで発見。実装箇所を `SpeakerDiarizationService` ＋ `TranscriptionService.TranscribeFileAsync` (catch) へ訂正し、例外が Error イベントへ変換される経路も本文に明記した。文書のみの修正でソース変更なし (2026-08-22)
- [x] **T115** 話者識別（Speaker Diarization）を sherpa-onnx で導入した。**ファイル文字起こし経路のみ**（sherpa-onnx のオフライン Diarization は音声全体を 1 配列で要求するためライブ経路には載らない）。既定は無効で、`settings.json` で有効化する。出力は `[時刻 - 時刻] [ファイル] [話者N] テキスト`。話者の割り当ては開始時刻ではなく**重複時間**で決め、同点は小さい話者 ID、重複ゼロは `[話者不明]`（直前の話者を引き継がない）。ADR-0001 に従いインターフェース抽象は作らず、sherpa-onnx 依存を `SpeakerDiarizationService` 1 クラスに閉じ、マージ規則は `internal static` の純粋関数にしてテスト 38 件で固定した。**調査で判明した地雷:** モデル未配置時に sherpa-onnx は NULL ハンドルを返すが C# ラッパーが検査せず、使うとアクセス違反で**プロセスが即死する**（catch 不能）ため、生成前の `File.Exists` 検査を必須にした (2026-08-22) → [ADR-0003](../adr/0003-speaker-diarization-with-sherpa-onnx.md) / [詳細](./T115-speaker-diarization.md)
- [x] **T109** `README.md` の「ビルド」節が存在しない `AudioCaptureApp.sln` を参照していた（正: `AudioCaptureApp.slnx`）。T108 の作業中に発見。当該 1 行を `.slnx` へ修正（`docs/archive/` 内の同種の記述は参照専用のため対象外） (2026-08-22)
- [x] **T133** ハーネスが `docs/spec/` 以外の場所に仕様文書が作られることを禁じていない。実際に `docs/superpowers/specs/2026-08-19-silence-cut-before-whisper-design.md`（T112 の設計文書）が生まれ、内容は `docs/tasks/T112-silence-cut-before-whisper.md` と重複したうえ T125 / T129 の改訂を取り込んでいない古い記述になっていた。50-spec-standards §1 と 00-ways-of-working 法 2 に禁止規則を明記し、当該ファイルを削除した。CLAUDE.md の「ドキュメントの置き場」表にも同じ禁止を明記 (2026-08-22)
- [x] **T131** `docs/spec/04_sequence_diagram.md` のライブ文字起こしの図が「1,000 行超は先頭から破棄」と書いているが、T130 で上限は 100 行になっており `REQ-LIVEVIEW-04` と食い違う（T130 が 01_requirements.md しか直さなかった）。T129 の作業中に発見。図を 100 行へ修正 (2026-08-21)
- [x] **T132** `docs/spec/01_requirements.md` の §8 で `REQ-TRX-LIVE-06` が 2 行に重複して振られている（`_sessionClock` の要件と、`REQ-TRX-LIVE-11` と内容が重なる「セッション終了処理は最大 30 秒待機」の要件）。ID は再利用しない規約（50-spec-standards §3）に反する。後者は REQ-TRX-LIVE-11 に吸収して行ごと消すのが妥当。T129 の作業中に発見。重複していた後者の行を削除し REQ-TRX-LIVE-11 に一本化 (2026-08-21)
- [x] **T129** ライブ文字起こしの出力粒度が 20 秒固定で、「発話が終わった」契機が存在しなかった。マイクは無音でも WASAPI が供給を続けるためギャップ分割（500ms）が発火せず、`StaleBufferAge`（20 秒）も `BufferThresholdSamples`（20 秒）と同値のため連続供給では常に空振りしていた。確定契機に「末尾に `MergeGapSeconds`（2.0 秒）以上の無音が積まれたら確定する」を追加し（REQ-TRX-LIVE-13）、滞留契機を「バッファ先頭サンプルの滞留」から「供給の途絶 5 秒」へ定義し直した（REQ-TRX-LIVE-12、定数も `StaleSupplyIdle` へ改名）。保持時間を `MergeGapSeconds` と同値にしたため Whisper の呼び出し回数は据え置き。実測: 発話終了から表示まで 0〜20 秒 → 3〜4 秒以内、1 発話が複数行に割れないことも確認。T114 の実機確認で発覚 (2026-08-21) → [詳細](./T129-live-transcript-endpointing.md)
- [x] **T130** 文字起こし表示ウィンドウの改良（初期サイズ 480x240 / ボタン文言「文字起こし表示」→「表示」/ 録音開始成功時に表示内容をクリア / 表示上限 1,000 行 → 100 行）。T114 §8-1 の未解決事項「表示をクリアする手段が無い」はこれで解決 (2026-08-21) → [詳細](./T130-live-transcript-window-tweaks.md)
- [x] **T128** 文字起こし表示ウィンドウで `ScrollIntoView` を `CollectionChanged` ハンドラー内から同期的に呼んでおり、`ItemContainerGenerator` の状態と食い違って `InvalidOperationException`（「ItemsControl が項目のソースと一致していません」）でプロセスが落ちる。1 チャンクから複数行が連続で届くと再現。`Dispatcher.BeginInvoke(Background)` へ後回しにして解消（実測: 修正前 3/3 クラッシュ → 修正後 0/3）。T114 の実機確認で発覚 (2026-08-21) → [詳細](./T128-live-transcript-scroll-crash.md)
- [x] **T114** リアルタイム文字起こしのテキストを表示するサブウィンドウを追加する（320x240・リサイズ可・9pt・録音停止で閉じない・プロセス終了で閉じる）。`SegmentTranscribed` を初めて購読した (2026-08-20) → [詳細](./T114-live-transcript-window.md)
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
