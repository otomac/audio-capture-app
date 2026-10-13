# T188 — 確定パスに文脈プロンプト（用語集＋直前の確定文）を渡す

> **状態:** 完了 — 2026-10-12
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **設計:** [T181](./T181-two-pass-live-transcription-design.md) D13 / D14、[ADR-0007](../adr/0007-two-pass-live-transcription.md) 決定 6、実測は [T182](./T182-provisional-pass-measurement.md) の M6 / E5

## 1. 目的

ライブの確定パスの呼び出しのたびに「用語集＋両音源の直近の確定文の末尾約 100 字」をプロンプトとして渡し、確定の文の質を上げる（REQ-TRX-LIVE-19 / REQ-CFG-13）。
T182 の M6 で文字誤り率が 34.7% → 31.7% に下がった組み立て方をそのまま移す。あわせて、Whisper が行頭に付ける「(山本)」のような話者名の付け札を落とす（利用者の依頼。台帳の追記 2026-10-11）。

## 2. スコープ境界

**やること**
- `TranscriptionService` に文脈プロンプト（組み立ての純粋関数・呼び出しのたびの processor）と、行頭の付け札の除去
- 設定 `TranscriptionGlossary`（設定ウィンドウ）・`LiveContextPromptEnabled`（`settings.json` のみ）
- 設定ウィンドウの ⑧ 用語集。項目が増えて縦に長くなるので、画面の高さを超えるときはスクロールさせる

**やらないこと（重要）**
- **速報・ファイル文字起こしにはプロンプトを渡さない**（T181 D14）
- **ファイル文字起こしの行の付け札は落とさない**（ライブの確定パスだけ。ファイル側は別タスクで判断する → T194）
- **確定パスの区切り方・`.txt` の書式は変えない**

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 用語集の組み立て | 1 行 1 語。前後の空白を除き、空行は捨てる。「、」で連結し、**120 文字を超える語から先は使わない**（語の単位）。1 語以上あれば末尾に「。」を付ける（M6 と同じ） |
| D2 | 直前の確定文 | 両音源の確定行のうち**今の区間の始まりより前に終わった行だけ**（T195 で追加）を**終わりの時刻順**に並べ、間が 30 秒以上空いた所より後ろだけを使う。今の区間の始まりが最後の行から 30 秒以上離れていれば使わない。壊れた行（空・定型の幻聴・繰り返し。速報の除去と同じ判定）は除き、本文を連結して**末尾 100 文字**を取る（M6 と同じ） |
| D3 | プロンプト | 用語集＋直前の確定文。どちらも空なら渡さない（音源ごとに作り置きの processor をそのまま使う ＝ 今と同じ） |
| D4 | processor | プロンプトがあるときは呼び出しのたびに作って破棄する（Whisper.net 1.9.0 は呼び出しごとにネイティブ状態を作り直すので生成は軽い。ADR-0007「現状」） |
| D5 | 直前の確定文の保持 | ワーカースレッドだけが触る一覧。録音開始で空にする。直近 50 行だけ持つ（末尾 100 文字に足りる） |
| D6 | 行頭の付け札 | **ライブの確定行の行頭の、空白を含まない 1〜10 文字の丸括弧書き（`(山本)` / `（大橋）`）を落とす**。落として空になる行は出さない（`.txt` にも画面にも）。直前の確定文にも付け札を落とした本文を入れる — 付け札がプロンプトに入ると次の呼び出しでも同じ名前が出やすくなるため。M6 では用語集なしで 275 行中 58 行、用語集ありで 243 行中 32 行に付き、名前が用語集の人名に置き換わった（「(大橋)」）。存在しない話者名を確定文に残すのは誤った情報になるため、落とすほうを採る |
| D7 | 有効化 | `LiveContextPromptEnabled`（既定 `true`。`settings.json` のみ）。用語集だけを使う設定は作らない（M6 で悪化した。E5）。付け札の除去は有効化によらず行う |
| D8 | 用語集の入力 | 設定ウィンドウの複数行のテキストボックス。入力のたびに保存する（`UpdateSourceTrigger=PropertyChanged`。Esc で閉じても失われない）。次の録音から効く |
| D9 | 設定ウィンドウの高さ | 中身をスクロール領域に入れ、ウィンドウの高さを作業領域の高さまでに抑える（「閉じる」はスクロールの外に置く）。項目が増えて 768 ピクセルの画面に収まらなくなるため |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-LIVE-19 の実装箇所と D2 / D3 / D5、REQ-TRX-LIVE-20（新設: 付け札の除去）、REQ-CFG-12（`LiveContextPromptEnabled`）、REQ-CFG-13、REQ-SETWIN-03 ⑧ と D9
- [x] `docs/spec/02_architecture.md` — §5.2.1 の文脈プロンプトの行
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService` / `SettingsViewModel` / `AppSettings` の新メンバー
- [x] `docs/spec/04_sequence_diagram.md` — §3 の区間ループにプロンプトと付け札の除去

## 5. アーキテクチャへの影響

- ADR: 不要 — ADR-0007（承認済み）の決定 6 の実装。スレッド・依存方向は変えない。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.Context.cs` | 新規（partial）。プロンプトの組み立て・付け札の除去・直前の確定文 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `StartSession` で準備、`TranscribeRegion` でプロンプト付きの processor と付け札の除去 |
| `AudioCaptureApp/Models/AppSettings.cs` | `TranscriptionGlossary` / `LiveContextPromptEnabled` |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 設定の写しと保存 |
| `AudioCaptureApp/ViewModels/SettingsViewModel.Context.cs` | 新規（partial）。用語集 |
| `AudioCaptureApp/ViewModels/SettingsViewModel.cs` | 初期値 |
| `AudioCaptureApp/SettingsWindow.xaml` | ⑧ 用語集、スクロール |
| `AudioCaptureApp.Tests/ContextPromptTests.cs` | 新規 |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値 |

## 7. 実装手順

### グループ A — サービス
- [x] **A1** 純粋関数: `BuildGlossaryPrompt` / `BuildContextPrompt` / `StripSpeakerTag`
- [x] **A2** `StartSession` で用語集のプロンプトを作り、直前の確定文を空にする
- [x] **A3** `TranscribeRegion` でプロンプト付きの processor・付け札の除去・直前の確定文への追加

### グループ B — 設定
- [x] **B1** 設定キー、`MainViewModel` の写しと保存
- [x] **B2** `SettingsViewModel` の用語集、設定ウィンドウの ⑧ とスクロール

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す
- [x] **Z5** 実モデルの通し（スクラッチの検証プログラム）でプロンプトありの確定パスが動くこと、付け札が残らないこと
- [x] **Z6** 同一プロセスの UI 検証で設定ウィンドウの XAML が落ちないこと・見え方

## 8. テスト一覧

- **`BuildGlossaryPrompt_JoinsTermsWithinLimitByWord`** — 「、」で連結し、120 文字を超える語から先を捨て、末尾に「。」
- **`BuildGlossaryPrompt_EmptyOrBlank_ReturnsEmpty`**
- **`BuildContextPrompt_GlossaryFirstThenTailOf100Chars`** — 用語集が先頭、確定文は末尾 100 文字
- **`BuildContextPrompt_OrdersBothSourcesByEndTime`** — 両音源の行を終わりの時刻順に並べる
- **`BuildContextPrompt_ResetsAfter30SecondsOfSilence`** — 30 秒の間より前を使わない／今の区間が 30 秒以上離れていれば使わない
- **`BuildContextPrompt_SkipsBrokenLines`** — 繰り返し・定型の幻聴を入れない
- **`BuildContextPrompt_NothingToSay_ReturnsNull`**
- **`StripSpeakerTag_*`** — `(山本)` / `（大橋）` を落とす、付け札だけの行は空、途中の括弧・長い括弧書き・空白を含む括弧書きは残す
- **`DefaultValues_AreCorrect`**（改訂）

> **テストで守れない範囲:** プロンプトによる文の質の変化（T182 の M6 で実測済み）。Z5 で動くことだけを確かめる。

## 9. 未解決の質問

なし（D6 の付け札の除去は利用者の依頼に対する推奨案として採った。ファイル側は T194 で判断する）。

## 10. 前提

- T185（確定パスの位置）と T182 の M6 の結果。

---

## 実行結果 (2026-10-12)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 0 件 / エラー 0 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分なし（終了コード 0）
- `dotnet test AudioCaptureApp.slnx -c Debug`  : 492 件成功 / 0 件失敗 / 0 件スキップ（T187 完了時 472 件 + 本タスク 20 件）
- **Z5 実モデルの通し**（スクラッチの検証プログラム。確定 small・GPU、速報 base・CPU、T182 の `ma02` を 300 秒、用語集は M6 と同じ 19 語）: 確定 64 行・**行頭に付け札が残った行 0 件**・エラー 0 件。用語集の語（「ワークスロップ」「プロジェクト機能」）が確定文に出た。プロンプト付きの processor を呼び出しのたびに作っても停止は 6.8 秒（滞留 20 秒）で、速報の遅れは p50 0.64 / p95 1.40 秒
- **Z6 同一プロセスの UI 検証**: 用語集を入力のたびに保存しサービスへ写す。設定ウィンドウは 901 ピクセル（作業領域 1392）。高さを 600 に抑えると中身がスクロールし（スクロール量 301）、「閉じる」は見える。画像で並びを確かめた。`settings.json` は復元してバイト一致
- 計画からの逸脱: なし
- **付け札の除去の効果（T191 の計測の出力で確かめた）:** プロンプト無し・small・GPU・10 秒チャンクの `ma02`（15.8 分）で、付け札を落とす前 34.72% → 落とした後 33.78%（275 行中 58 行に付け札）。T182 の M6 の「なし」34.7% と同じ条件
- 未確認: プロンプトありでの文の質の変化は測り直していない（T182 の M6 で 34.7% → 31.7%）
- **後で見つかった不具合（T195 で修正済み）:** 2 音源が同じ音声を拾うと、もう一方の音源が同じ時間帯を確定した文がプロンプトに入り、確定パスの費用がほぼ倍になって行も欠けた。直前の確定文を「今の区間の始まりより前に終わった行」に絞った（D2 を改訂）
