# T153 — 文字起こしの言語を指定できるようにする

> **状態:** 完了 — 2026-08-24
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

Whisper へ渡す言語が `WithLanguage("ja")` で 3 か所にハードコードされている。
ライブとファイルで**それぞれ独立に**指定できるようにし、設定に残す。

## 2. スコープ境界

**やること**
- ライブ文字起こしの言語をメインウィンドウのドロップダウンで選ぶ。
- ファイル文字起こしの言語をオプション指定ダイアログのドロップダウンで選ぶ。
- 2 つは**独立**に保持し、`settings.json` に永続化する。

**やらないこと（重要）**
- **ライブ側に「自動判定」を出さない**（D2）。
- **Whisper の 99 言語をすべて並べない**（D1）。
- **翻訳（`WithTranslate`）は扱わない。** 言語の指定だけで、出力言語の変換はしない。
- **無音カット・チャンク分割・話者識別の挙動を変えない。** 言語は
  `WhisperProcessorBuilder` への指定だけに効く。
- **既存の `settings.json` を書き換えない。** 未設定なら既定（日本語）として読む。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 選択肢の範囲 | **日本語・英語（＋ファイル側のみ自動判定）**（利用者の指示 2026-08-23）。99 言語は並べない。増やしたくなったら別タスクで起票する |
| D2 | 「自動判定」をどちらに出すか | **ファイル側だけに出す**（利用者の指示 2026-08-24）。ライブ経路は 20 秒チャンクで、短い入力では言語検出が誤りやすいと台帳が指摘しているが、**その誤検出率は実測していない**。実測には実音声と Whisper モデルによる手動検証が要るため、確かめずに出すことはしない |
| D3 | 実装 API | **`WithLanguageDetection()`（自動判定）と `WithLanguage(code)`（明示）を出し分ける。** `WithLanguage("auto")` に頼らない — Whisper.net が自動判定用の API を持っているため、そちらを使う |
| D4 | 保存先 | **`AppSettings.LiveTranscriptionLanguage` / `FileTranscriptionLanguage` の 2 つ。** 既定はどちらも `ja`（従来のハードコードと同じ挙動） |
| D5 | 不正値の扱い | **`settings.json` に未知のコードが書かれていたら既定（`ja`）へ倒す。** `SilenceCutOptions` / `SpeakerDiarizationOptions` と同じく、手編集され得る値は必ず正規化する。ライブ側で `auto` が書かれていた場合も `ja` へ倒す（D2 により選べないため） |
| D6 | 変更の反映時期 | **ライブは次に録音を開始したときから効く**（`RegisterSource` で `WhisperProcessor` を作るため）。録音中は変更できない（`IsNotBusy` で無効化）。**ファイルは「開始」を押した時点の選択が使われる** |
| D7 | ファイル側の 2 経路 | **話者識別の有無にかかわらず同じ言語を使う。** ハードコードは 3 か所（ライブ 1・ファイル 2）あり、ファイルの 2 か所（通常経路と話者識別経路）は同じ設定から取る |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md`
  - §7 に **REQ-TRX-10**（言語指定の基盤・正規化・自動判定の出し分け）を新設
  - §8 に **REQ-TRX-LIVE-14**（ライブ側 UI）を新設
  - §9 に **REQ-TRX-FILE-16**（ファイル側 UI）を新設
  - §6 に **REQ-CFG-07**（2 つの言語設定の永続化）を新設
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionLanguage` / `TranscriptionLanguages`、
      `TranscriptionService` の言語プロパティと引数、`MainViewModel` の選択プロパティ
- [ ] `docs/spec/02_architecture.md` — 影響なし
- [ ] `docs/spec/04_sequence_diagram.md` — 影響なし（呼び出し順序は変わらない）

## 5. アーキテクチャへの影響

- ADR: **不要。** 依存も層も増えない。言語の一覧と正規化は、`SilenceCutOptions` /
  `SpeakerDiarizationOptions` と同じく Service 層の設定オプション型として置く。

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/TranscriptionService.cs` | `TranscriptionLanguage` / `TranscriptionLanguages` を追加。`LiveLanguage` プロパティと `TranscribeFileAsync` の言語引数を追加し、`WithLanguage("ja")` 3 か所を置き換え |
| `AudioCaptureApp/Models/AppSettings.cs` | 言語設定 2 つを追加 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | 選択肢・選択中の言語・保存・サービスへの反映 |
| `AudioCaptureApp/MainWindow.xaml` | ライブ言語のドロップダウン |
| `AudioCaptureApp/FileTranscriptionOptionsWindow.xaml` | ファイル言語のドロップダウン |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | 一覧と正規化のテスト |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値のテスト |
| `docs/spec/01_requirements.md` / `03_class_diagram.md` | 上記のとおり |

## 7. 実装手順

### グループ A — 仕様書
- [x] **A1** REQ-TRX-10 / REQ-TRX-LIVE-14 / REQ-TRX-FILE-16 / REQ-CFG-07 を追加
- [x] **A2** クラス図へ反映

### グループ B — Service / Model
- [x] **B1** `TranscriptionLanguage` と `TranscriptionLanguages` を追加
- [x] **B2** `AppSettings` に言語設定 2 つを追加
- [x] **B3** `WithLanguage("ja")` 3 か所を設定由来へ置き換え

### グループ C — ViewModel
- [x] **C1** 選択肢と選択中の言語を追加し、変更時に保存・反映する

### グループ D — View
- [x] **D1** メインウィンドウにライブ言語のドロップダウンを追加
- [x] **D2** ダイアログにファイル言語のドロップダウンを追加

### グループ Z — 検証
- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新反映を読み直す

## 8. テスト一覧

- **`Languages_Live_DoesNotOfferAutoDetection`** — ライブの選択肢に自動判定が無い（D2）
- **`Languages_File_OffersAutoDetection`** — ファイルの選択肢に自動判定がある
- **`Languages_AllOptions_HaveCodeAndDisplayName`** — 空の項目が紛れ込まない
- **`NormalizeForLive_UnknownCode_FallsBackToJapanese`** — 手編集された不正値で壊れない（D5）
- **`NormalizeForLive_Auto_FallsBackToJapanese`** — ライブでは `auto` を受け付けない（D5）
- **`NormalizeForFile_Auto_IsKept`** — ファイルでは `auto` を残す
- **`NormalizeForFile_UnknownCode_FallsBackToJapanese`**
- **`Normalize_NullOrBlank_FallsBackToJapanese`** — 未設定の `settings.json` で従来どおり日本語
- **`Languages_FirstOption_IsJapanese`** — 既定値・フォールバック先が従来のハードコードと同じ
- **`Normalize_MixedCaseAndPadding_IsAccepted`** — 大小・空白のゆれを受理する
- **`AppSettingsTests.DefaultValues_AreCorrect`（追記）** — 既定は従来のハードコードと同じ挙動（D4）

> **テストで守れない範囲:** `WithLanguage` / `WithLanguageDetection` を実際に渡したときの
> Whisper の出力（言語が本当に切り替わるか、自動判定がどれだけ当たるか）は
> **実モデルと実音声が要るため検証しない。** 手動確認が要る。
> とくに **D2 の根拠となる「20 秒チャンクでの誤検出率」は測っていない。**

## 9. 未解決の質問

なし（D1〜D7 で確定）。ただし §8 のとおり、自動判定の実力は未実測である。
ライブ側にも出すかどうかは、実測してから別タスクで判断する。

## 10. 前提

- Whisper.net 1.9.0 の `WhisperProcessorBuilder` に `WithLanguageDetection()` がある
  （パッケージの XML ドキュメントで確認済み）。
- 言語コードは 2 文字（`ja` / `en`）。Whisper が受け付ける表記に合わせる。

---

## 実行結果 (2026-08-24)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 209 件成功 / 0 件失敗 / 0 件スキップ（うち T153 追加分 14 件）
- 計画からの逸脱: `LiveLanguageOptions` / `FileLanguageOptions` は式形式のプロパティにすると
  CA1822（static にできる）でビルドが落ちるため、初期化子付きの自動プロパティにした。
  WPF のバインディングはインスタンスプロパティを要求するので、static 化はできない
- 手動確認が要る範囲: 言語を切り替えたときに Whisper の出力が実際に変わること、
  自動判定の当たり具合（§8 のとおり実モデル・実音声が要るため未検証）
