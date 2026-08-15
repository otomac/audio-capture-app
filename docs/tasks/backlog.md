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

- [ ] **T109** `README.md` の「ビルド」節が存在しない `AudioCaptureApp.sln` を参照している（正: `AudioCaptureApp.slnx`）。T108 の作業中に発見

## 完了

- [x] **T108** 実行基盤を .NET 8 から .NET 10 へ引き上げる（アプリ／テスト／CI） (2026-08-15) → [詳細](./T108-net10-upgrade.md)
- [x] **T107** `SettingsService` のフィールド初期化をコンストラクターに集約（宣言時とコンストラクターへの分散を解消） (2026-08-15)

- [x] **T106** `.claude/settings.local.json` を git 管理から外す (2026-08-14) → [詳細](./T106-untrack-local-settings.md)
- [x] **T105** 軽微な静的解析指摘の解消（CA1822 / CA1825） (2026-08-14) → [詳細](./T105-minor-analyzer-findings.md)
- [x] **T104** 非同期の是正 — 同期呼び出しの解消と `CancellationToken` の伝播（CA1849 / CA2016） (2026-08-14) → [詳細](./T104-async-discipline.md)
- [x] **T103** `catch (Exception)` の局所化と理由付与（CA1031） (2026-08-14) → [詳細](./T103-narrow-exception-handling.md)
- [x] **T102** カルチャー・文字列比較の明示（CA1305 / CA1307） (2026-08-14) → [詳細](./T102-culture-and-string-comparison.md)
- [x] **T101** Dispose パターンの是正（CA1063 / CA1816 / CA1001 / CA2213 / CA2000） (2026-08-14) → [詳細](./T101-dispose-pattern-cleanup.md)
- [x] **T100** 開発ハーネスの整備（`docs/harness/` の規範・強制フック・ビルドゲート） (2026-08-14) → [詳細](./T100-dev-harness.md)
