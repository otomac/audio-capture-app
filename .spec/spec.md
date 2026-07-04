# Feature: 001-windows-audio-capture

Created: 2026-03-02
Status: Draft
Input: Windows上で動作するスタンドアローンの音声キャプチャアプリ。OSの音声デバイスを指定できるUIを持ち、選択した入力デバイスの音声をMP3ファイルに保存する。Windowsミキサー経由で動作し、デバイスを占有しない。C# / WPF で実装する。

---

## Status Update

- **Updated: 2026-07-04** - 仕様書追記（実装済み追加機能）
- **Previous Status: Draft** → 現在の実装状況に合わせてスコープ拡張

---

## User Scenarios & Testing

### User Story 1 - 音声デバイスを選択して録音を開始する (Priority: P1)

録音を始める前に、どのマイク・デバイスを使うかを選びたい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: ドロップダウンリストに表示されたデバイス一覧から任意の入力デバイスを選択する
- Then: 選択したデバイスが録音対象として設定される

- Given: 録音対象デバイスを選択している
- When: 「録音開始」ボタンを押す
- Then: 録音が開始され、UIが録音中であることを示す（ボタン状態・インジケータ等）

**Edge Cases**
- 入力デバイスが1つも接続されていない場合 → 「デバイスが見つかりません」を表示しボタンを無効化
- アプリ起動後にデバイスが接続・切断された場合 → 「更新」ボタンで一覧を再取得できる

---

### User Story 2 - 録音を停止してMP3ファイルを保存する (Priority: P1)

録音したデータを任意のフォルダにMP3ファイルとして保存したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: 録音中である
- When: 「録音停止」ボタンを押す
- Then: 録音が停止し、指定の保存先フォルダに MP3 ファイルが出力される
- And: ファイル名は `YYYYMMDD_HHmmss.mp3` の形式で自動生成される

**Edge Cases**
- 保存先フォルダが存在しない場合 → フォルダを自動作成する
- ディスク残量不足の場合 → エラーメッセージを表示して録音を安全に停止する
- 録音時間が0秒（即停止）の場合 → ファイルを出力しない

---

### User Story 3 - 保存先フォルダを指定する (Priority: P2)

デフォルト保存先以外のフォルダに保存先を変更したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: 「保存先を選択」ボタンを押してフォルダダイアログでフォルダを選択する
- Then: 選択したフォルダが保存先として設定され、UIにパスが表示される
- And: 設定はアプリ再起動後も保持される

**Edge Cases**
- フォルダダイアログでキャンセルした場合 → 保存先は変更しない

---

### User Story 4 - 他アプリと同じデバイスを共有録音する (Priority: P1)

Zoom や Teams などが使用中のマイクであっても、同時に録音できる。

**Acceptance Scenarios (Given/When/Then)**

- Given: 他のアプリが同じ入力デバイスを使用している
- When: 録音を開始する
- Then: デバイスを占有せず（WASAPI Shared Mode）、双方が正常に動作する

**Edge Cases**
- 排他モードで他アプリがデバイスを占有している場合 → エラーメッセージを表示する

---

### User Story 5 - スピーカー出力を同時に録音する (Priority: P2)

マイクだけでなく PC のスピーカーから出ている音声も同時に記録したい（ループバックキャプチャ）。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: スピーカーデバイスを選択し、マイクと同時に録音を開始する
- Then: マイクとスピーカー音声が 1 つの MP3 ファイルに混合される

- Given: 録音中である
- When: スピーカー用のミュートボタンを操作する
- Then: マイク音声には影響せず、スピーカー音声のみミュートされる

**Edge Cases**
- スピーカーデバイスが利用できない場合 → マイクのみで録音できる
- ループバックキャプチャをサポートしないシステム → 代替手段で対応またはエラー表示

---

### User Story 6 - マイクのミュート状態をシステムと同期する (Priority: P2)

Windows のマイクミュート設定と アプリのミュート状態を常に同期させたい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: Windows システムのマイクミュートボタン（ハードウェアキーまたは OS 設定）を操作する
- Then: アプリの UI が自動的に同期され、ミュート状態が反映される

- Given: アプリのマイクミュートボタンを操作している
- When: ボタンをクリックしてミュートを ON/OFF する
- Then: Windows OS のミュート状態も同時に変更される

**Edge Cases**
- デバイスが AudioEndpointVolume インターフェースをサポートしていない場合 → ソフトウェアミュートのみで動作
- OS 折り返し通知による無限ループ → 抑止機構で防止

---

### User Story 7 - 音声ファイルから自動で文字起こしする (Priority: P2)

音声ファイル（WAV / MP3）を選択して、自動的に日本語テキストに変換したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: Whisper モデルが読み込まれている
- When: 「音声ファイルから文字起こし」ボタンをクリックして音声ファイルを選択する
- Then: ファイルが処理され、同名の .txt ファイルが同じフォルダに保存される

- Given: 文字起こし処理中である
- When: UI に進捗が表示される
- Then: 処理時間とおおよその完了までの時間が確認できる

**Edge Cases**
- Whisper モデルが読み込まれていない場合 → ボタンが無効化される
- 対応していないファイル形式を選択した場合 → エラーメッセージを表示
- 処理中に「中止」ボタンをクリック → 処理を中断し、部分的な結果は保存しない

---

### User Story 8 - ドラッグ&ドロップで文字起こしを開始する (Priority: P2)

ファイルダイアログを開かず、音声ファイルをウィンドウにドラッグ&ドロップして直接文字起こししたい。

**Acceptance Scenarios (Given/When/Then)**

- Given: Whisper モデルが読み込まれている
- When: 音声ファイルを文字起こしグループボックスにドラッグ&ドロップする
- Then: 自動的に文字起こしが開始される

- Given: ファイルがドラッグされている
- When: グループボックスの上でホバーされている
- Then: ドロップ受け入れ可能な状態を示すビジュアルがハイライトされる

**Edge Cases**
- 非対応のファイル（画像、PDF など）がドロップされた場合 → エラーメッセージを表示
- 複数ファイルが同時にドロップされた場合 → 最初のファイルのみ処理、または一覧で処理

---

### User Story 9 - リアルタイム音量レベルを確認する (Priority: P2)

マイクおよびスピーカーからの音声信号の強さをリアルタイムで dB 単位で表示したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: 音量ミキサーグループボックスを見る
- Then: マイクとスピーカーそれぞれのピークレベルがメーターで表示される

- Given: マイクに音声が入力されている
- When: マイクミュートボタンを ON にする
- Then: メーターがすぐに 0 dB（無音）に下がる

**Edge Cases**
- デバイスからデータが送られてこない場合 → -60 dB の下限を表示
- 音声信号が過大（クリッピング）の場合 → +3 dB の上限で表示

---

### User Story 10 - GPU を活用して文字起こしを高速化する (Priority: P3)

Whisper の推論を GPU で実行して、文字起こしの処理時間を短縮したい（ハードウェアが対応している場合）。

**Acceptance Scenarios (Given/When/Then)**

- Given: CUDA 対応の GPU がシステムに搭載されている
- When: Whisper モデルを読み込む
- Then: GPU を自動検出して活用される

- Given: GPU で推論中である
- When: システムが対応している場合
- Then: CPU のみの場合と比べて 3 倍以上高速に処理される

**Edge Cases**
- CUDA をサポートしていない GPU → 自動的に CPU フォールバック
- GPU メモリ不足 → CPU で継続処理
- ドライバが古い → 警告を表示して CPU で処理

---

## Functional Requirements

- FR-001: Windows 10 以降で動作するスタンドアローンの .exe アプリとして配布できる
- FR-002: アプリ起動時に Windows の音声入力デバイス一覧をドロップダウンに表示する
- FR-003: ドロップダウンで選択したデバイスを録音対象にできる
- FR-004: WASAPI Shared Mode で録音する（デバイスを占有しない）
- FR-005: 録音データを MP3 形式（LAME エンコード）でファイルに保存する
- FR-006: ファイル名を `YYYYMMDD_HHmmss.mp3` 形式で自動生成する
- FR-007: 保存先フォルダをフォルダダイアログで指定できる
- FR-008: デフォルト保存先は `%USERPROFILE%\Documents\AudioCapture` とする
- FR-009: 保存先フォルダ設定をアプリ設定ファイル（JSON）に永続化する
- FR-010: 録音中であることを UI で視覚的に示す（ボタン状態の切り替えを含む）
- FR-011: デバイス一覧の手動更新（再スキャン）ができる
- FR-012: 録音中の経過時間をリアルタイムで表示する
- FR-013: スピーカー（ループバック）デバイスを選択して、マイク音声と混合して録音できる
- FR-014: マイクのミュート状態を Windows OS の AudioEndpointVolume インターフェースで監視・制御する
- FR-015: OS からのハードウェアミュート通知を受け取り、UI に反映する（双方向同期）
- FR-016: ソフトウェアミュート機能により、OS 設定が取得できないデバイスでもミュート録音に対応する
- FR-017: Whisper（OpenAI の音声文字起こしモデル）を GGML 形式で読み込める
- FR-018: 音声ファイル（WAV / MP3）を Whisper で処理して、テキストファイル（.txt）に出力する
- FR-019: ドラッグ&ドロップで音声ファイルをアプリに直接ドロップして文字起こしを開始できる
- FR-020: マイクとスピーカー音声のピークレベルをリアルタイムで dB 単位で測定・表示する
- FR-021: Whisper 推論時に CUDA 対応 GPU を自動検出して活用し、CPU のみの場合より高速化する
- FR-022: GPU メモリ不足時は自動的に CPU フォールバックして処理を継続する
- FR-023: 文字起こし処理中にキャンセルボタンで中止でき、部分結果は保存しない

---

## Key Entities

### AudioDevice
- `DeviceId` (string): WASAPI デバイス識別子
- `FriendlyName` (string): UI 表示用のデバイス名
- `IsDefault` (bool): デフォルトデバイスかどうか

### RecordingSession
- `FilePath` (string): 出力 MP3 のフルパス
- `StartedAt` (DateTime): 録音開始時刻
- `StoppedAt` (DateTime?): 録音停止時刻（録音中は null）
- `DeviceId` (string): 使用デバイスの識別子

### AppSettings
- `OutputFolder` (string): 保存先フォルダパス
- `LastSelectedDeviceId` (string?): 前回選択したマイク デバイス ID
- `LastSelectedLoopbackDeviceId` (string?): 前回選択したスピーカー デバイス ID
- `TranscriptionEnabled` (bool): ライブ文字起こしを有効にするか
- `WhisperModelPath` (string?): Whisper GGML モデルファイルのフルパス

### TranscriptionSession
- `FilePath` (string): 入力音声ファイルのフルパス
- `TranscriptPath` (string): 出力テキストファイルのフルパス（同名 .txt）
- `StartedAt` (DateTime): 文字起こしセッション開始時刻
- `CompletedAt` (DateTime?): 完了時刻（処理中は null）
- `ProcessedDuration` (TimeSpan): 処理済み音声の長さ
- `TotalDuration` (TimeSpan): 入力ファイルの総長さ

### AudioLevelMetrics
- `MicPeakLevel` (float): マイク信号のピークレベル（0.0 ～ 1.0）
- `LoopbackPeakLevel` (float): スピーカー信号のピークレベル（0.0 ～ 1.0）
- `MicLevelDb` (double): マイクレベル（dB、-60 ～ +3）
- `LoopbackLevelDb` (double): スピーカーレベル（dB、-60 ～ +3）

---

## Success Criteria

- SC-001: アプリ起動後 3 秒以内に入力デバイス一覧が表示される
- SC-002: 録音開始ボタン押下から 1 秒以内に実際の録音が始まる
- SC-003: 録音停止後、指定フォルダに有効な MP3 ファイルが存在する
- SC-004: 他アプリが同デバイスを使用中でも録音が開始できる（WASAPI Shared Mode 確認）
- SC-005: アプリ再起動後も保存先フォルダの設定が維持される
- SC-006: 1時間以上の録音で MP3 ファイルが破損せず再生できる
- SC-007: Windows マイクミュートボタン押下から 500ms 以内に UI が同期される
- SC-008: ハードウェアミュート同期により、アプリ側の書き込みによる無限ループが起こらない
- SC-009: ファイル文字起こし処理で、1 分の音声ファイルが 30 秒以内に処理される（GPU 使用時）
- SC-010: 文字起こしテキストファイルが正しく UTF-8 で保存される
- SC-011: ドラッグ&ドロップ時のドロップエリアハイライトが 100ms 以内に表示される
- SC-012: ピークレベルメーターが 50ms 間隔でリアルタイムに更新される
- SC-013: GPU フォールバック時でも処理が継続し、ユーザー操作は一切失われない
