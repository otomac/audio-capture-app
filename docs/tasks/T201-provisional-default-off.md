# T201 — 速報パスの既定を OFF にする（コードは残す）

> **状態:** 完了 — 2026-11-23
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **関連:** [ADR-0009](../adr/0009-single-pass-light-model-live-transcription.md)（承認 2026-11-23）/ [T200](./T200-live-chunk-settings.md)（同じブランチで行う。利用者の指示 2026-11-23）

## 1. 目的

ADR-0009 の決定「速報パスは既定 OFF にし、コードは残す」の実装。

## 2. スコープ境界

**やること**
- `AppSettings.LiveProvisionalEnabled` の既定を `true` → `false` にする
- 仕様書（REQ-CFG-12・REQ-TRX-LIVE-15・`02_architecture.md` §4 のスレッド表・`05_transcribe_logic.html`）を先に直す

**やらないこと（重要）**
- **速報のコード・設定ウィンドウの切り替え・速報用モデルの選択は変えない**（ON にすれば今までどおり動く）
- **既存の `settings.json` に `true` が保存されていても書き換えない**（台帳の既定案。利用者が設定ウィンドウで OFF にする）
- **速報のコードを消さない**（1 段の構成を実際の会議で使ってから利用者が決める。別タスク）

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 既存の設定 | **書き換えない。** キーの無い古い設定（速報の導入前）だけが新しい既定（OFF）で読まれる |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-CFG-12（既定 `false`）、REQ-TRX-LIVE-15（既定 OFF を明記）
- [x] `docs/spec/02_architecture.md` — §4 スレッド表の `ProvisionalTranscription`（速報を ON にしたときだけ起動。既定 OFF）
- [x] `docs/spec/05_transcribe_logic.html` — 「動かすには」の既定、速報パスの節の既定

## 5. アーキテクチャへの影響

- ADR: 不要（ADR-0009 で承認済み。スレッドの種類は減らさない）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Models/AppSettings.cs` | `LiveProvisionalEnabled` の既定を `false` に |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値のテストを `false` に |

## 7. 実装手順

- [x] **A1** 仕様書を直す
- [x] **A2** 既定を `false` にし、テストを直す
- [x] **Z1〜Z3** 品質ゲート（T200 とまとめて実行）

## 8. テスト一覧

- **`AppSettingsTests` の既定値のテスト（変更）** — `LiveProvisionalEnabled` の既定が `false`

> **テストで守れない範囲:** 設定ウィンドウのチェックボックスが既定で外れて表示されること（バインドの見た目）。

## 9. 未解決の質問

台帳の既定案（既存の `true` は書き換えない）を採った。利用者の指示（2026-11-23）で既定案のまま進める。

---

## 実行結果 (2026-11-23)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 527 件成功 / 0 件失敗 / 0 件スキップ（T200 とまとめて実行）
- 計画からの逸脱: なし（保存済みの `true` を書き換えないことを固定するテスト `JsonDeserialization_SavedProvisionalTrue_IsKept` と、キーが無ければ OFF で読むことの確認を足した）