# T196 — 文字起こしのモデルと調整値の関係を、利用者目線の仕様書にまとめる

> **状態:** 完了 — 2026-10-17
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

利用者の依頼 (2026-10-17): 文字起こしに関わる各種モデルとパラメーターの関係を、この音声キャプチャアプリの仕様として説明する。
読み手は数学・工学の高度な知識を持たないが、時間軸・マイクとスピーカーの区別・速報と確定の意味は分かる。
必要なパラメーターはすべて具体的な値で示し、`docs/spec/05_transcribe_logic.html`（HTML 形式）として出力する。

## 2. スコープ境界

**やること**
- `docs/spec/05_transcribe_logic.html` を新設する（モデルの役割分担・時間軸・確定パス・速報パス・ファイル文字起こし・話者識別・GPU / CPU・設定値の一覧）
- `docs/spec/00_overview.md` の「ドキュメント構成」に 05 を足す
- `docs/harness/50-spec-standards.md` §2 の対応表に 05 の行を足す（いつ 05 を更新するか）

**やらないこと（重要）**
- **アプリの振る舞い・コード・既定値は変えない。** 本章は現在のコード（2026-10-17 の `develop`、789d56d）の事実を書く
- **`01_requirements.md` 〜 `04_sequence_diagram.md` の本文は直さない。** 作業中に見つけた食い違いは別タスクに起票する（T197 / T198）
- **新しい要件 ID を採番しない。** 本章は既存の要件を束ね直した解説であり、要件の定義は 01 が持つ

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 形式 | **HTML 1 ファイル（利用者の指定）。外部のスクリプト・フォント・画像を読まない** — リポジトリを開いた環境でオフラインでも同じに見えるようにする。フォントは Windows 標準（アプリと同じ Yu Gothic UI 系）、図はインライン SVG |
| D2 | 01_requirements との関係 | **定義は 01 が持ち、本章は利用者目線で束ね直した解説とする。** 値ごとに対応する要件 ID を小さく添え、食い違ったら「仕様書が古い」バグとして起票する（優先順位はコード → 01 → 本章）。本文は利用者の言葉で書き、クラス名・メソッド名は出さない（`settings.json` のキー名は利用者が編集する対象なので出す） |
| D3 | 本アプリが指定しない Whisper のデコード設定 | **「Whisper.net 1.9.0 の既定のまま」と書き、具体値を推測で書かない。** 既定値はコードから読めず、確かめていないため |
| D4 | 図 | **4 枚に絞る**: ①全体の流れ（どの音声がどのモデルへ行き、どこへ出るか）②録音中のタイムライン（マイク / スピーカー、速報の句と確定のチャンク）③1 チャンクの中の無音カット ④速報の句の開き方・閉じ方 |
| D5 | 「いつ効くか」の書き方 | **すぐ／次の録音から／次の起動から／ダイアログの「開始」で の 4 種で書く。** `settings.json` の値は起動時にだけ読まれ、アプリが保存するたびに書き戻されるため、手で編集するときはアプリを閉じてからにする旨を書く |

## 4. 仕様書への影響

- [x] `docs/spec/05_transcribe_logic.html` — 新設
- [x] `docs/spec/00_overview.md` — 「ドキュメント構成」に 05 を追加
- 変更なし: `01_requirements.md` 〜 `04_sequence_diagram.md`（振る舞いを変えないため）

## 5. アーキテクチャへの影響

- ADR: **不要**。文書の追加だけで、層構成・依存方向・スレッドモデル・ライブラリに触れない

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `docs/spec/05_transcribe_logic.html` | 新設 |
| `docs/spec/00_overview.md` | 「ドキュメント構成」の表に 05 の行を追加 |
| `docs/harness/50-spec-standards.md` | §2 の対応表に 05 の行を追加（文字起こしのモデル・区切り方・既定値・許容範囲を変えたら更新） |
| `docs/tasks/backlog.md` | T196 を起票。作業中に見つけた T197 / T198 を起票 |
| `docs/tasks/T196-transcribe-logic-spec.md` | 本票 |

## 7. 実装手順

### グループ A — 事実の確認
- [x] **A1** `TranscriptionService.cs` / `.Provisional.cs` / `.Context.cs`・`ProvisionalPhrasePlanner.cs`・`ProvisionalOptions.cs`・`SpeakerDiarizationService.cs`・`TranscriptDiarizationMerger.cs`・`AppSettings.cs`・`MainViewModel.cs`（設定の受け渡し）・`LiveTranscriptViewModel.cs`（字幕帯）・`AudioCaptureService.cs`（ミュート時の供給）から定数と既定値を拾う
- [x] **A2** `01_requirements.md` §6〜§15・ADR-0007 と突き合わせる

### グループ B — 文書
- [x] **B1** `docs/spec/05_transcribe_logic.html` を書く
- [x] **B2** `00_overview.md` と `50-spec-standards.md` に 05 を足す

### グループ Z — 検証（必須・最後に置く）
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 05 に書いた数値をコードの定数と読み合わせる

## 8. テスト一覧

追加・変更なし（文書だけの変更）。

> **テストで守れない範囲:** 05 の数値とコードの一致は自動では検査されない。値を変えるタスクは 50-spec-standards.md §2 の対応表に従い 05 も更新する。

## 9. 未解決の質問

なし。

## 10. 前提

- 2026-10-17 時点の `develop`（789d56d）の挙動を書く。T182 などの実測値は、それぞれのタスク票に記録された値をそのまま引く（本タスクでは測り直さない）。

---

## 実行結果 (2026-10-17)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし（終了コード 0）
- `dotnet test`  : 507 件成功 / 0 件失敗 / 0 件スキップ
- 文書の検査: 05 の相対リンク 5 件すべて実在。目次のアンカー 11 件すべて実在。Edge（ヘッドレス、幅 1200px・ライト）で描画し、図 4 枚の文字の重なりと表の崩れが無いことを目で確認した
- 読み合わせ（Z4）: 05 の数値を `TranscriptionService*.cs`・`ProvisionalPhrasePlanner.cs`・`ProvisionalOptions.cs`・`SpeakerDiarizationService.cs`・`AppSettings.cs`・`MainViewModel*.cs`・`LiveTranscriptViewModel.cs` の定数・既定値と突き合わせた。下書きにあった「`SilenceMergeGapSeconds` を 0 にすると話し終えた所で区切らなくなる」は誤りだった（`ChunkTakeCount` は末尾の無音が 0 より長ければ確定する）ので、「確認のたびに区切り、細切れになる」に直した
- 計画からの逸脱: なし。作業中に見つけた食い違い 2 件は T197（REQ-TRX-LIVE-11 の古い注記）・T198（ファイル文字起こしの案内に `.m4a` が無い）として起票し、このタスクでは直していない
