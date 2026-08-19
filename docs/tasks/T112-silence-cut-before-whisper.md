# T112 — 無音区間を Whisper に渡さないようにカットする

> **状態:** 完了 — 2026-08-20
> **台帳:** [docs/tasks/backlog.md](./backlog.md)
> **ブランチ:** `feature-silence-cut-before-whisper`

## 1. 目的

チャンクを「渡すか捨てるか」の二択で扱う現行の `IsSilent` を、**有声区間の集合に切り分けて
区間ごとに Whisper へ渡す**方式へ置き換え、無音部でのハルシネーション
（「ご視聴ありがとうございました」等）を抑止する。T116 §9 が残した既知のトレードオフの解消。

## 2. スコープ境界

**やること**
- `TranscriptionService` に有声区間分割 `SplitVoicedRegions` を追加し、`IsSilent` を削除する
- ライブ（`ProcessChunk`）とファイル（`ProcessFileChunkAsync`）の両方を区間ループ化する
- 閾値・結合幅・余白の 3 つを `settings.json` から与えられるようにする（UI は作らない）

**やらないこと（重要）**
- **20 秒チャンクの切り出し方（`TakeNextChunk` / `ChunkTakeCount` / ギャップ分割）は変えない。**
  本タスクは「切り出したチャンクを Whisper に渡す直前」だけに手を入れる
- **`PadToMinimum` の 1 秒パディングは変えない**
- **UI は一切追加しない**（設定は `settings.json` の直接編集で調整する）
- **`AudioCaptureService`（録音・ミキシング・MP3 書き出し）には触らない**
- **話者識別（T115）・リアルタイム表示サブウィンドウ（T114）には触らない**

## 3. 決定事項

実装中に蒸し返さない。変更するときは本セクションを直してから。

| # | 決定 | 結論 |
|---|---|---|
| D1 | 無音のカット方式 | **有声区間ごとに分割して個別に Whisper へ渡す。** 連結して 1 本にする案は `segment.Start` が実時刻へ写像できなくなり T116 の時刻保証を壊すため却下 |
| D2 | 有声区間をつなぐ無音の長さ | **2.0 秒。** 日本語会話の息継ぎ（0.3〜1.5 秒）で文を分断せず、推論回数の増加も抑える |
| D3 | 区間の前後に付ける余白 | **200ms。** RMS の立ち上がりは実際の語頭より遅れるため |
| D4 | 切り出さない最小長 | **0.2 秒**（パディング前の区間長で判定）。※ ③で結合した場合は結合後の区間全体の長さで判定するため、内部に取り込んだ無音も含まれる。この性質が T125 の元になっている |
| D5 | 足切りとパディングの順序 | **足切りが先。** 逆順だと 0.1 秒（＝窓 1 つ分。窓量子化により、これが存在しうる最短の区間）のクリック音が余白込みで 0.5 秒になり足切りを素通りする |
| D6 | 連続発話時の扱い | **区間合計が元チャンクの 90% 以上なら分割せず全体を 1 区間として返す。** 無音をほぼ落とせないのに推論回数だけ増えるのを防ぐ |
| D7 | 適用範囲 | **ライブとファイルの両方。** 片方だけ振る舞いが違うのは仕様として不整合 |
| D8 | パラメータの持ち方 | **`settings.json`（`AppSettings` に 3 件）。** 窓長 100ms・最小区間 0.2 秒・非分割率 90% は内部機構なので定数のまま |
| D9 | 設定の消失対策 | **読み込んだ `AppSettings` インスタンスを `MainViewModel` が保持し、`SaveSettings` はそのインスタンスを更新して保存する。** 現行の「毎回 new する」方式のままだと UI を持たない設定が保存のたびに消える |
| D10 | `IsSilent` の去就 | **削除する。** 窓ごとの判定は `SplitVoicedRegions` の内部に残るので REQ-TRX-06 の趣旨は保たれる。二重スキャンになるだけの前置きガードは置かない |

## 4. 仕様書への影響

- [x] `docs/spec/01_requirements.md` — REQ-TRX-06 を「窓ごとの有声判定」へ書き換え、REQ-TRX-09（有声区間分割）と REQ-CFG-06（3 設定）を追加。**あわせて REQ-TRX-08 の「チャンク」を「区間」へ、REQ-CFG-01 の列挙に調整値を追加**（レビュー指摘。区間が 1 秒未満のとき `PadToMinimum` を通すことが仕様から読み取れなくなっていた）
- [x] `docs/spec/02_architecture.md` — 層構成・スレッドモデル・データフローは変わらないが、**§ Services の `internal static` ヘルパー例に `IsSilent` が挙がっていた**ので `SplitVoicedRegions` へ差し替え（当初「影響なし」としていたのは誤り）
- [x] `docs/harness/20-architecture-standards.md` — 同様に「副作用のない計算」の既存例から `IsSilent` を `SplitVoicedRegions` へ差し替え（規範そのものは変えていない。例示の事実誤りの訂正のみ）
- [x] `docs/spec/03_class_diagram.md` — `TranscriptionService` から `IsSilent` を削り `SplitVoicedRegions` と `SilenceCut` を足す。`VoicedRegion` / `SilenceCutOptions` を追加。**`AppSettings` ブロックにも 3 プロパティを追加**（レビュー指摘。01 と 03 が矛盾していた）。末尾の `internal static` ヘルパー注記も更新
- [x] `docs/spec/04_sequence_diagram.md` — 「3. ライブ文字起こし」を区間分割と `loop 有声区間ごと` へ書き換え。**「6. ファイルからの文字起こし」にも `IsSilent` の参照が残っていたので同様に書き換え**（当初の計画が見落としていた）。ファイル側は行ごとに `WriteLineAsync` する点でライブ側と異なるため、バッチ書き込みにしない

## 5. アーキテクチャへの影響

なし。Models / ViewModels / Services の 3 層と依存方向は変わらず、新規 NuGet パッケージも追加しない。
`SplitVoicedRegions` は `TranscriptionService` 内の静的純粋関数で、テストから直接呼べる。

- ADR: **不要**（層構成・依存方向・スレッドモデル・外部ライブラリのいずれにも触れないため）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `docs/spec/01_requirements.md` | REQ-TRX-06 改訂、REQ-TRX-09 / REQ-CFG-06 追加 |
| `docs/spec/03_class_diagram.md` | `SplitVoicedRegions` / `VoicedRegion` / `SilenceCutOptions` 追加、`IsSilent` 削除 |
| `docs/spec/04_sequence_diagram.md` | ライブ文字起こしの無音判定を区間ループへ |
| `AudioCaptureApp/Models/AppSettings.cs` | `SilenceRmsThreshold` / `SilenceMergeGapSeconds` / `VoicedPaddingSeconds` を追加 |
| `AudioCaptureApp/Services/TranscriptionService.cs` | `SilenceCutOptions` / `VoicedRegion` / `SplitVoicedRegions` / `SilenceCut` を追加、`IsSilent` を削除、`ProcessChunk` と `ProcessFileChunkAsync` を区間ループ化 |
| `AudioCaptureApp/ViewModels/MainViewModel.cs` | `AppSettings` インスタンス保持、`SaveSettings` のインスタンス更新化、`SilenceCut` の反映 |
| `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` | `IsSilent` の 11 テストを `SplitVoicedRegions` 版へ移行、区間分割・クランプのテストを追加 |
| `AudioCaptureApp.Tests/AppSettingsTests.cs` | 既定値と JSON ラウンドトリップに 3 項目を追加 |

## 7. 実装手順

上から順に実行する。グループごとにビルドを通す。テストを先に書く（TDD）。

### グループ A — 仕様書を先に直す（法 2）

- [x] **A1** `docs/spec/01_requirements.md` の REQ-TRX-06 を書き換える（実装箇所を `TranscriptionService.SplitVoicedRegions` に）
- [x] **A2** 同ファイルに REQ-TRX-09（有声区間分割：結合 2.0 秒・足切り 0.2 秒・余白 200ms・非分割率 90%・足切りが先）を追加
- [x] **A3** 同ファイルに REQ-CFG-06（3 設定の既定値とクランプ、UI 非提供）を追加
- [x] **A4** `docs/spec/03_class_diagram.md` を §4 のとおり更新
- [x] **A5** `docs/spec/04_sequence_diagram.md` を §4 のとおり更新

### グループ B — `SilenceCutOptions`（設定の受け皿とクランプ）

- [x] **B1** `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` に失敗するテストを書く

```csharp
[Fact]
public void SilenceCutOptions_Defaults_MatchSpec()
{
    var options = SilenceCutOptions.Default;

    Assert.Equal(0.01, options.RmsThreshold);
    Assert.Equal(2.0, options.MergeGapSeconds);
    Assert.Equal(0.2, options.PaddingSeconds);
}

[Theory]
[InlineData(double.NaN)]
[InlineData(double.PositiveInfinity)]
[InlineData(double.NegativeInfinity)]
public void SilenceCutOptions_NonFiniteValues_FallBackToDefaults(double bad)
{
    var options = new SilenceCutOptions(bad, bad, bad);

    Assert.Equal(SilenceCutOptions.Default.RmsThreshold, options.RmsThreshold);
    Assert.Equal(SilenceCutOptions.Default.MergeGapSeconds, options.MergeGapSeconds);
    Assert.Equal(SilenceCutOptions.Default.PaddingSeconds, options.PaddingSeconds);
}

[Fact]
public void SilenceCutOptions_OutOfRangeValues_AreClamped()
{
    var options = new SilenceCutOptions(-1.0, -5.0, 999.0);

    Assert.Equal(0.0, options.RmsThreshold);
    Assert.Equal(0.0, options.MergeGapSeconds);
    Assert.Equal(5.0, options.PaddingSeconds);
}
```

- [x] **B2** 実行して失敗を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~SilenceCutOptions"
```

期待: コンパイルエラー（`SilenceCutOptions` が存在しない）

- [x] **B3** `AudioCaptureApp/Services/TranscriptionService.cs` に `SilenceCutOptions` を実装する

```csharp
/// <summary>
/// 無音カットの調整値。settings.json から与えられるため、
/// 手書きされた不正値（負値・NaN・無限大・過大値）でも壊れないよう
/// コンストラクターで必ずクランプする。
/// </summary>
// 実装時に sealed record（クラス）へ変更した。詳細は末尾「計画からの逸脱」#4。
public sealed record SilenceCutOptions
{
    private const double DefaultRmsThreshold = 0.01;
    private const double DefaultMergeGapSeconds = 2.0;
    private const double DefaultPaddingSeconds = 0.2;

    /// <summary>余白の上限。1 チャンク（20 秒）に対して現実的な範囲に収める。</summary>
    private const double MaxPaddingSeconds = 5.0;

    /// <summary>結合幅の上限。チャンク長（20 秒）を超えても意味が無い。</summary>
    private const double MaxMergeGapSeconds = 20.0;

    public SilenceCutOptions(double rmsThreshold, double mergeGapSeconds, double paddingSeconds)
    {
        RmsThreshold = Sanitize(rmsThreshold, 0.0, 1.0, DefaultRmsThreshold);
        MergeGapSeconds = Sanitize(mergeGapSeconds, 0.0, MaxMergeGapSeconds, DefaultMergeGapSeconds);
        PaddingSeconds = Sanitize(paddingSeconds, 0.0, MaxPaddingSeconds, DefaultPaddingSeconds);
    }

    /// <summary>有声とみなす窓の RMS 下限。</summary>
    public double RmsThreshold { get; }

    /// <summary>これ未満の無音を挟む有声区間どうしは 1 区間に結合する。</summary>
    public double MergeGapSeconds { get; }

    /// <summary>各有声区間の前後に付ける余白。</summary>
    public double PaddingSeconds { get; }

    public static SilenceCutOptions Default { get; } =
        new(DefaultRmsThreshold, DefaultMergeGapSeconds, DefaultPaddingSeconds);

    // 非有限値は「設定が壊れている」とみなして既定値へ戻す。
    // Math.Clamp は NaN をそのまま返すため、先に弾く必要がある。
    private static double Sanitize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
```

- [x] **B4** 実行して成功を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~SilenceCutOptions"
```

期待: 5 件成功（`Default` 1 件 ＋ `NonFinite` 3 件 ＋ `OutOfRange` 1 件）

### グループ C — `SplitVoicedRegions`（本体）

サンプルは 16kHz。窓 = 1600 サンプル（100ms）、余白 = 3200 サンプル（0.2 秒）、
足切り = 3200 サンプル（0.2 秒）、結合 = 32000 サンプル（2.0 秒）。

- [x] **C1** テストヘルパーと最初の失敗するテストを書く（`AudioCaptureApp.Tests/TranscriptionServiceTests.cs`）

```csharp
/// <summary>16kHz の無音バッファを作り、指定範囲だけ有声（振幅 0.5）にする。</summary>
private static float[] MakeSamples(int totalSamples, params (int Start, int Length)[] voiced)
{
    var samples = new float[totalSamples];
    foreach (var (start, length) in voiced)
    {
        for (int i = start; i < start + length; i++)
        {
            samples[i] = 0.5f;
        }
    }
    return samples;
}

[Fact]
public void SplitVoicedRegions_AllSilent_ReturnsEmpty()
{
    // 20 秒すべて無音 → Whisper に何も渡さない（ハルシネーション源を渡さない）
    var samples = new float[16000 * 20];

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    Assert.Empty(regions);
}

[Fact]
public void SplitVoicedRegions_EmptyChunk_ReturnsEmpty()
{
    var regions = TranscriptionService.SplitVoicedRegions([], SilenceCutOptions.Default);

    Assert.Empty(regions);
}
```

- [x] **C2** 実行して失敗を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~SplitVoicedRegions"
```

期待: コンパイルエラー（`SplitVoicedRegions` / `VoicedRegion` が存在しない）

- [x] **C3** `TranscriptionService` に `VoicedRegion` と `SplitVoicedRegions` を実装する

```csharp
/// <summary>チャンク内の有声区間。Start はチャンク先頭からのサンプル位置。</summary>
public readonly record struct VoicedRegion(int Start, int Length);

// SilenceWindowSamples（100ms 窓）は既存の定数をそのまま使う。新たに宣言しないこと
// （既に TranscriptionService に存在するため、再宣言するとコンパイルエラーになる）。
// XML コメントだけ「無音判定の窓長」から「有声／無音を判定する窓長」へ直す。

/// <summary>
/// これ未満の有声区間は切り出さない（0.2 秒）。パディング前の長さで判定する。
/// 値は <see cref="MinTailSamples"/> と同じだが根拠が別（あちらはセッション終端の
/// 残バッファをどこまで処理するかの閾値）なので、定数は共有せず別に持つ。
/// </summary>
internal const int MinVoicedSamples = TargetRate / 5;

/// <summary>
/// 有声区間の合計がチャンクのこの割合以上なら分割しない。
/// 落とせる無音がわずかなのに Whisper の呼び出し回数だけ増えるのを防ぐ。
/// </summary>
internal const double NoSplitVoicedRatio = 0.9;

/// <summary>
/// チャンクを有声区間の集合に切り分ける。長い無音を含まない区間列を返す。
/// </summary>
/// <remarks>
/// 手順は次の順。<b>足切り（4）はパディング（5）より先に行う。</b>
/// 逆順だと 0.05 秒のクリック音でも余白込みで 0.45 秒になり、足切りを素通りする。
/// <list type="number">
/// <item>100ms 窓ごとに RMS を求め、閾値以上の窓を有声とする</item>
/// <item>連続する有声窓を 1 区間にまとめる</item>
/// <item>間隔が MergeGapSeconds 未満の区間どうしを結合する</item>
/// <item><see cref="MinVoicedSamples"/> 未満の区間を捨てる</item>
/// <item>前後に余白を付け、チャンク端でクランプし、重なったら再結合する</item>
/// <item>合計が <see cref="NoSplitVoicedRatio"/> 以上ならチャンク全体を 1 区間として返す</item>
/// </list>
/// 窓ごとに判定するのは、チャンク全体の平均 RMS では長い無音に埋もれた短い発話が
/// ならされて無音扱いになるため（T116）。
/// </remarks>
internal static IReadOnlyList<VoicedRegion> SplitVoicedRegions(
    float[] samples, SilenceCutOptions options)
{
    if (samples.Length == 0)
    {
        return [];
    }

    var regions = CollectVoicedWindows(samples, options.RmsThreshold);
    if (regions.Count == 0)
    {
        return [];
    }

    MergeCloseRegions(regions, SecondsToSamples(options.MergeGapSeconds));
    regions.RemoveAll(r => r.Length < MinVoicedSamples);
    if (regions.Count == 0)
    {
        return [];
    }

    ApplyPadding(regions, SecondsToSamples(options.PaddingSeconds), samples.Length);

    // パディングで区間が接触・交差しうるので、隙間ゼロのものを畳む。
    MergeCloseRegions(regions, 1);

    long voicedTotal = 0;
    foreach (var region in regions)
    {
        voicedTotal += region.Length;
    }

    return voicedTotal >= samples.Length * NoSplitVoicedRatio
        ? [new VoicedRegion(0, samples.Length)]
        : regions;
}

private static int SecondsToSamples(double seconds) => (int)(seconds * TargetRate);

/// <summary>100ms 窓ごとに RMS を判定し、連続する有声窓を 1 区間にまとめる。</summary>
private static List<VoicedRegion> CollectVoicedWindows(float[] samples, double threshold)
{
    var regions = new List<VoicedRegion>();
    int runStart = -1;

    for (int start = 0; start < samples.Length; start += SilenceWindowSamples)
    {
        int length = Math.Min(SilenceWindowSamples, samples.Length - start);

        double sumSquares = 0;
        for (int i = start; i < start + length; i++)
        {
            sumSquares += samples[i] * (double)samples[i];
        }

        if (Math.Sqrt(sumSquares / length) >= threshold)
        {
            if (runStart < 0)
            {
                runStart = start;
            }
        }
        else if (runStart >= 0)
        {
            regions.Add(new VoicedRegion(runStart, start - runStart));
            runStart = -1;
        }
    }

    if (runStart >= 0)
    {
        regions.Add(new VoicedRegion(runStart, samples.Length - runStart));
    }

    return regions;
}

/// <summary>間隔が maxGap 未満の隣接区間を結合する。</summary>
private static void MergeCloseRegions(List<VoicedRegion> regions, int maxGap)
{
    for (int i = regions.Count - 1; i > 0; i--)
    {
        var previous = regions[i - 1];
        int gap = regions[i].Start - (previous.Start + previous.Length);
        if (gap < maxGap)
        {
            int end = regions[i].Start + regions[i].Length;
            regions[i - 1] = new VoicedRegion(previous.Start, end - previous.Start);
            regions.RemoveAt(i);
        }
    }
}

/// <summary>各区間の前後に余白を付け、チャンクの範囲内へクランプする。</summary>
private static void ApplyPadding(List<VoicedRegion> regions, int padding, int totalSamples)
{
    for (int i = 0; i < regions.Count; i++)
    {
        int start = Math.Max(0, regions[i].Start - padding);
        int end = Math.Min(totalSamples, regions[i].Start + regions[i].Length + padding);
        regions[i] = new VoicedRegion(start, end - start);
    }
}
```

- [x] **C4** 実行して成功を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~SplitVoicedRegions"
```

期待: 2 件成功

- [x] **C5** 振る舞いのテストを追加する（`AudioCaptureApp.Tests/TranscriptionServiceTests.cs`）

```csharp
[Fact]
public void SplitVoicedRegions_SilenceWithShortNoise_ReturnsOnlyNoiseRegion()
{
    // T116 §9 のトレードオフ解消そのもの。
    // 20 秒の無音の中で 10 秒地点に 0.5 秒の物音 → その周辺だけを渡し、19.5 秒の無音は渡さない。
    var samples = MakeSamples(16000 * 20, (160000, 8000));

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    var region = Assert.Single(regions);
    Assert.Equal(160000 - 3200, region.Start);
    Assert.Equal(8000 + 6400, region.Length);
}

[Fact]
public void SplitVoicedRegions_TwoUtterancesWithLongGap_ReturnsTwoRegions()
{
    // 0〜1 秒と 5〜6 秒に発話（間隔 4 秒 ≧ 2 秒）→ 分割される
    var samples = MakeSamples(16000 * 10, (0, 16000), (80000, 16000));

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    Assert.Equal(2, regions.Count);
    Assert.Equal(0, regions[0].Start);
    Assert.Equal(16000 + 3200, regions[0].Length);
    Assert.Equal(80000 - 3200, regions[1].Start);
    Assert.Equal(16000 + 6400, regions[1].Length);
}

[Fact]
public void SplitVoicedRegions_ShortGap_MergesIntoOneRegion()
{
    // 0〜1 秒と 2〜3 秒に発話（間隔 1 秒 < 2 秒）→ 文を割らないよう結合される
    var samples = MakeSamples(16000 * 10, (0, 16000), (32000, 16000));

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    var region = Assert.Single(regions);
    Assert.Equal(0, region.Start);
    Assert.Equal(48000 + 3200, region.Length);
}

[Fact]
public void SplitVoicedRegions_AddsPaddingAroundVoiced()
{
    // 5〜6 秒に発話 → 前後 200ms の余白が付き、語頭・語尾が欠けない
    var samples = MakeSamples(16000 * 10, (80000, 16000));

    var region = Assert.Single(
        TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

    Assert.Equal(80000 - 3200, region.Start);
    Assert.Equal(16000 + 6400, region.Length);
}

[Fact]
public void SplitVoicedRegions_PaddingClampedToChunkBounds()
{
    // チャンク先頭から発話 → 余白でマイナスに飛び出さない
    var samples = MakeSamples(16000, (0, 8000));

    var region = Assert.Single(
        TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

    Assert.Equal(0, region.Start);
    Assert.True(region.Start + region.Length <= samples.Length);
    Assert.Equal(8000 + 3200, region.Length);
}

[Fact]
public void SplitVoicedRegions_MostlyVoiced_ReturnsSingleFullRegion()
{
    // 0〜9 秒と 11〜20 秒に発話（間隔ちょうど 2 秒なので結合されない）。
    // 余白込みの有声合計は 92% ≧ 90% → 分割せずチャンク全体を 1 区間で返す
    var samples = MakeSamples(16000 * 20, (0, 144000), (176000, 144000));

    var region = Assert.Single(
        TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

    Assert.Equal(0, region.Start);
    Assert.Equal(samples.Length, region.Length);
}

[Fact]
public void SplitVoicedRegions_ContinuousSpeech_ReturnsSingleFullRegion()
{
    // 全区間が発話 → 現状と同じく 1 回の推論、同じ文脈長で動く
    var samples = MakeSamples(16000 * 20, (0, 16000 * 20));

    var region = Assert.Single(
        TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

    Assert.Equal(0, region.Start);
    Assert.Equal(samples.Length, region.Length);
}

[Fact]
public void SplitVoicedRegions_ClickShorterThanMinimum_IsDropped()
{
    // 100ms の単発クリック音（< 0.2 秒）だけ → 捨てる。
    // 足切りをパディングより後に行うと 0.1 + 0.4 = 0.5 秒になり素通りするため、
    // このテストが D5（足切りが先）を守る。
    var samples = MakeSamples(16000 * 10, (48000, 1600));

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    Assert.Empty(regions);
}

[Fact]
public void SplitVoicedRegions_VoicedAtMinimumLength_IsKept()
{
    // ちょうど 0.2 秒の発話 → 短くても取りこぼさない（T116 の「捨てすぎを直す」を後退させない）
    var samples = MakeSamples(16000 * 10, (48000, 3200));

    var region = Assert.Single(
        TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

    Assert.Equal(48000 - 3200, region.Start);
    Assert.Equal(3200 + 6400, region.Length);
}

[Fact]
public void SplitVoicedRegions_QuietRoomNoise_ReturnsEmpty()
{
    // 閾値未満の環境ノイズ（RMS 0.005）だけ → 無音として扱い、渡さない
    var samples = new float[16000 * 20];
    for (int i = 0; i < samples.Length; i++)
    {
        samples[i] = 0.005f;
    }

    var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

    Assert.Empty(regions);
}

[Fact]
public void SplitVoicedRegions_ZeroThreshold_TreatsEverythingAsVoiced()
{
    // 閾値 0 は「カットしない」設定として成立させる（クランプ下限が 0 のため到達しうる）
    var samples = new float[16000 * 10];
    var options = new SilenceCutOptions(0.0, 2.0, 0.2);

    var region = Assert.Single(TranscriptionService.SplitVoicedRegions(samples, options));

    Assert.Equal(0, region.Start);
    Assert.Equal(samples.Length, region.Length);
}
```

- [x] **C6** 実行して全件成功を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~SplitVoicedRegions"
```

期待: 13 件成功

### グループ D — 呼び出し側を区間ループ化し、`IsSilent` を削除する

- [x] **D1** `TranscriptionService` に区間切り出しヘルパーと設定プロパティを追加する

```csharp
/// <summary>無音カットの調整値。MainViewModel が設定から反映する。</summary>
public SilenceCutOptions SilenceCut { get; set; } = SilenceCutOptions.Default;

/// <summary>区間を切り出す。チャンク全体と一致するならコピーせず元配列を返す。</summary>
private static float[] SliceRegion(float[] samples, VoicedRegion region)
    => region.Start == 0 && region.Length == samples.Length
        ? samples
        : samples[region.Start..(region.Start + region.Length)];
```

- [x] **D2** `ProcessChunk` の冒頭（`try` の中）を区間ループへ書き換える。`catch` 以降は現行のまま変更しない

```csharp
private void ProcessChunk(PendingChunk chunk, SourceState state, CancellationToken token)
{
    try
    {
        // 無音は Whisper に渡さない（ハルシネーション防止）。
        // 時刻は区間自身が持つため、無音を捨てても後続の時刻はずれない。
        var regions = SplitVoicedRegions(chunk.Samples, SilenceCut);
        if (regions.Count == 0)
        {
            return;
        }

        var results = new List<string>();
        foreach (var region in regions)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var regionStart = chunk.StartElapsed
                + TimeSpan.FromSeconds((double)region.Start / TargetRate);
            var samples = PadToMinimum(SliceRegion(chunk.Samples, region), MinWhisperSamples);
            TranscribeRegion(state, samples, regionStart, results, token);
        }

        if (results.Count > 0)
        {
            File.AppendAllLines(_outputPath, results, Encoding.UTF8);
        }
    }
```

- [x] **D3** `ProcessChunk` から切り出した `TranscribeRegion` を追加する

```csharp
/// <summary>
/// 有声区間 1 つを Whisper に掛け、整形した行を results へ積む。
/// regionStart は区間先頭の、セッション開始からの経過時間。
/// </summary>
private void TranscribeRegion(
    SourceState state, float[] samples, TimeSpan regionStart,
    List<string> results, CancellationToken token)
{
    // ProcessAsync を同期的に消費
    var asyncEnum = state.Processor!.ProcessAsync(samples, token);
    var enumerator = asyncEnum.GetAsyncEnumerator(token);
    try
    {
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            var segment = enumerator.Current;
            var text = segment.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var startTime = _sessionStartTime + regionStart + segment.Start;
            var endTime = _sessionStartTime + regionStart + segment.End;
            var line = $"[{startTime:HH:mm:ss} - {endTime:HH:mm:ss}] [{state.Label}] {text}";
            results.Add(line);
            SegmentTranscribed?.Invoke(line);
        }
    }
    finally
    {
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
```

- [x] **D4** `ProcessFileChunkAsync` を区間ループへ書き換える

```csharp
private async Task ProcessFileChunkAsync(
    WhisperProcessor processor, float[] samples, TimeSpan chunkOffset,
    string label, StreamWriter writer, CancellationToken ct)
{
    foreach (var region in SplitVoicedRegions(samples, SilenceCut))
    {
        ct.ThrowIfCancellationRequested();

        var regionOffset = chunkOffset + TimeSpan.FromSeconds((double)region.Start / TargetRate);
        var regionSamples = PadToMinimum(SliceRegion(samples, region), MinWhisperSamples);

        await foreach (var segment in processor.ProcessAsync(regionSamples, ct).ConfigureAwait(false))
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var startTime = regionOffset + segment.Start;
            var endTime = regionOffset + segment.End;
            var line = $"[{startTime:hh\\:mm\\:ss} - {endTime:hh\\:mm\\:ss}] [{label}] {text}";
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            SegmentTranscribed?.Invoke(line);
        }
    }

    await writer.FlushAsync(ct).ConfigureAwait(false);
}
```

- [x] **D5** `IsSilent` の 2 つのオーバーロードと `SilenceRmsThreshold` 定数を削除する
      （`SilenceWindowSamples` は `CollectVoicedWindows` が使うので残す）
- [x] **D6** `AudioCaptureApp.Tests/TranscriptionServiceTests.cs` から `IsSilent` の 11 テストを削除する
      （振る舞いは C5 の `SplitVoicedRegions_*` が引き継いでいる）
- [x] **D7** ビルドして未使用参照が残っていないことを確認する

```powershell
dotnet build AudioCaptureApp.slnx -c Debug
```

期待: 警告 0 件 / エラー 0 件

### グループ E — 設定の配線

- [x] **E1** `AudioCaptureApp.Tests/AppSettingsTests.cs` の `DefaultValues_AreCorrect` に追記する

```csharp
        Assert.Equal(0.01, settings.SilenceRmsThreshold);
        Assert.Equal(2.0, settings.SilenceMergeGapSeconds);
        Assert.Equal(0.2, settings.VoicedPaddingSeconds);
```

- [x] **E2** 同ファイルの `JsonRoundTrip_PreservesValues` の初期化に 3 項目を足し、対応する `Assert.Equal` を追記する

```csharp
            SilenceRmsThreshold = 0.02,
            SilenceMergeGapSeconds = 1.5,
            VoicedPaddingSeconds = 0.3
```

```csharp
        Assert.Equal(original.SilenceRmsThreshold, deserialized.SilenceRmsThreshold);
        Assert.Equal(original.SilenceMergeGapSeconds, deserialized.SilenceMergeGapSeconds);
        Assert.Equal(original.VoicedPaddingSeconds, deserialized.VoicedPaddingSeconds);
```

- [x] **E3** 実行して失敗を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~AppSettings"
```

期待: コンパイルエラー（3 プロパティが存在しない）

- [x] **E4** `AudioCaptureApp/Models/AppSettings.cs` に 3 プロパティを追加する

```csharp
    /// <summary>有声とみなす 100ms 窓の RMS 下限（-40dB 相当）。UI からは変更できない。</summary>
    public double SilenceRmsThreshold { get; set; } = 0.01;

    /// <summary>これ未満の無音を挟む有声区間どうしは結合する（秒）。UI からは変更できない。</summary>
    public double SilenceMergeGapSeconds { get; set; } = 2.0;

    /// <summary>有声区間の前後に付ける余白（秒）。UI からは変更できない。</summary>
    public double VoicedPaddingSeconds { get; set; } = 0.2;
```

- [x] **E5** 実行して成功を確認する

```powershell
dotnet test AudioCaptureApp.slnx -c Debug --filter "FullyQualifiedName~AppSettings"
```

期待: 3 件成功

- [x] **E6** `MainViewModel` に読み込んだ設定を保持するフィールドを追加する（`AudioCaptureApp/ViewModels/MainViewModel.cs`）

```csharp
    /// <summary>
    /// 読み込んだ設定そのもの。SaveSettings はこのインスタンスを更新して保存する。
    /// 毎回 new すると、UI を持たない設定項目（無音カットの調整値など）が
    /// 保存のたびに既定値へ戻ってしまうため。
    /// </summary>
    private readonly AppSettings _settings;
```

- [x] **E7** コンストラクターの `var settings = _settingsService.Load();` を `_settings = _settingsService.Load();` に変え、以降の `settings.` 参照をすべて `_settings.` に置き換える。あわせて無音カット設定をサービスへ反映する

```csharp
        _settings = _settingsService.Load();
        OutputFolder = _settings.OutputFolder;
        TranscriptionEnabled = _settings.TranscriptionEnabled;
        WhisperModelPath = _settings.WhisperModelPath;
        UseGpuForTranscription = _settings.UseGpuForTranscription;
        _transcriptionService.SilenceCut = new SilenceCutOptions(
            _settings.SilenceRmsThreshold,
            _settings.SilenceMergeGapSeconds,
            _settings.VoicedPaddingSeconds);
```

- [x] **E8** `SaveSettings` をインスタンス更新方式へ書き換える

```csharp
    private void SaveSettings()
    {
        // UI を持たない設定項目（無音カットの調整値など）を消さないため、
        // 読み込んだインスタンスの UI 対応プロパティだけを更新して保存する。
        _settings.OutputFolder = OutputFolder;
        _settings.LastSelectedDeviceId = SelectedCaptureDevice?.DeviceId;
        _settings.LastSelectedLoopbackDeviceId = SelectedRenderDevice?.DeviceId;
        _settings.TranscriptionEnabled = TranscriptionEnabled;
        _settings.WhisperModelPath = WhisperModelPath;
        _settings.UseGpuForTranscription = UseGpuForTranscription;
        _settingsService.Save(_settings);
    }
```

### グループ Z — 検証（必須・最後に置く）

- [x] **Z1** `dotnet build AudioCaptureApp.slnx -c Debug` — 警告 0 件
- [x] **Z2** `dotnet format AudioCaptureApp.slnx --verify-no-changes` — 差分なし
- [x] **Z3** `dotnet test AudioCaptureApp.slnx -c Debug` — 全件成功
- [x] **Z4** 仕様書（§4）の更新が実装とズレていないか読み直す
- [x] **Z5** 台帳の T112 を `[x]` にし、本ファイル末尾に実測値を記録する

## 8. テスト一覧

- **`SilenceCutOptions_Defaults_MatchSpec`** — 既定値が 0.01 / 2.0 / 0.2 である
- **`SilenceCutOptions_NonFiniteValues_FallBackToDefaults`** — NaN・±∞ で既定値へ戻る
- **`SilenceCutOptions_OutOfRangeValues_AreClamped`** — 負値・過大値がクランプされる
- **`SplitVoicedRegions_AllSilent_ReturnsEmpty`** — 完全無音は 1 区間も返さない
- **`SplitVoicedRegions_EmptyChunk_ReturnsEmpty`** — 空配列で落ちない
- **`SplitVoicedRegions_SilenceWithShortNoise_ReturnsOnlyNoiseRegion`** — T116 §9 のトレードオフ解消そのもの
- **`SplitVoicedRegions_TwoUtterancesWithLongGap_ReturnsTwoRegions`** — 2.0 秒以上空けば分割される
- **`SplitVoicedRegions_ShortGap_MergesIntoOneRegion`** — 2.0 秒未満は結合され、文が割れない
- **`SplitVoicedRegions_AddsPaddingAroundVoiced`** — 前後 200ms が付き、語頭が欠けない
- **`SplitVoicedRegions_PaddingClampedToChunkBounds`** — チャンク端で範囲外にならない
- **`SplitVoicedRegions_MostlyVoiced_ReturnsSingleFullRegion`** — 90% ガードが効き、推論回数が増えない
- **`SplitVoicedRegions_ContinuousSpeech_ReturnsSingleFullRegion`** — 連続発話は現状と同じ 1 回の推論で動く
- **`SplitVoicedRegions_ClickShorterThanMinimum_IsDropped`** — 0.2 秒未満を捨てる（D5 の順序を守る）
- **`SplitVoicedRegions_VoicedAtMinimumLength_IsKept`** — ちょうど 0.2 秒は取りこぼさない
- **`SplitVoicedRegions_QuietRoomNoise_ReturnsEmpty`** — 閾値未満の環境ノイズは渡さない
- **`SplitVoicedRegions_ZeroThreshold_TreatsEverythingAsVoiced`** — 閾値 0 は「カットしない」設定として成立する
- **`SplitVoicedRegions_GapExactlyMergeThreshold_DoesNotMerge`** — 結合判定が「未満」であることを固定（`<` を `<=` にすると落ちる）
- **`SplitVoicedRegions_PaddingCausesOverlap_MergesAfterPadding`** — パディング後の再結合。無いと同じ音声を 2 回 Whisper に渡す
- **`SplitVoicedRegions_ChunkLengthNotMultipleOfWindow_CoversToEnd`** — 窓長の倍数でないチャンクで末尾を落とさない
- **`SplitVoicedRegions_JustBelowNoSplitRatio_ReturnsSeparateRegions`** — 88% では分割したまま
- **`SplitVoicedRegions_ExactlyAtNoSplitRatio_ReturnsSingleFullRegion`** — ちょうど 90% は「以上」なので全体を返す
- **`SplitVoicedRegions_TwoClicksWithinMergeGap_AreMergedAndKept`** — T125 の既知の限界を可視化する記録テスト
- **`DefaultValues_AreCorrect`（改修）** — `AppSettings` の 3 既定値
- **`JsonRoundTrip_PreservesValues`（改修）** — 3 項目が JSON を往復する
- **`JsonDeserialization_MissingFields_UsesDefaults`（改修）** — 3 キーが無い既存 `settings.json` を読んでも 既定値が残る（`0.0` に束縛されると全ユーザーで無音カットが無効化される）
- **`RegionStart_*`（4 件・計画外の追加）** — 区間の開始時刻の合成式を固定する。レビューで、この式が 2 箇所に重複していてどちらもテストされておらず、`region.Start` を `region.Length` に変えても全件通ることが判明したため、1 つに集約して固定した

> **テストで守れない範囲:**
> - 実際の Whisper 推論を伴うハルシネーション抑止の効果はユニットテストで測れない
>   （モデルと実音声が要る）。**手動確認が必要。**
> - `MainViewModel.SaveSettings` はユニットテストで検証できない。**当初「`Application.Current.Dispatcher`
>   が要るので生成できない」と書いていたが、これは誤りだった**（実測: `Application.Current == null` の
>   MTA スレッドでもコンストラクターは成功する。`DispatcherTimer` は現在スレッドの Dispatcher を作り、
>   `Application.Current` を触る 2 つのラムダは構築時には呼ばれないため）。
>   実際の理由は **`MainViewModel` が `SettingsService` を `new` で直接持っており差し替え口が無い**こと。
>   `SettingsService` は `%APPDATA%\AudioCaptureApp\settings.json` を自前で決め打ちするため、
>   `SaveSettings` を叩くテストは**開発者の実ファイルを上書きする**。加えて `RefreshDevicesInternal` が
>   実オーディオデバイスを列挙し、`TryLoadWhisperModel` は `async void` で追跡されない背景処理を起こす。
>   差し替え口を作るのはコンストラクター注入の導入であり、ADR-0001 により **ADR が必要**なので本タスクでは行わない。
>   「設定が消えないこと」は D9 の設計（インスタンス保持）で構造的に担保する。
> - 推論回数が実際に減る／増えないことは計測を要するため、ユニットテストでは区間数でしか代替できない。

## 9. 未解決の質問

なし。判断が必要だった点は §3 の D1〜D10 で確定済み。

## 9-2. 実装中に発見し、別タスクへ送った事項

いずれもレビューで発見。T112 のスコープ内では直さず起票した（法 1）。

| ID | 内容 |
|---|---|
| T125 | 短い物音が結合幅（2.0 秒）内に複数あると 1 区間にまとまり、大半が無音のまま渡る。**「span でなく有声合計で判定」という素朴な修正は効かない**（窓量子化により結合区間は必ず 2 窓 = 0.2 秒以上になるため）。有声「密度」による足切りの検討が要る |
| T126 | `ProcessChunk` の `results` が `try` の内側にあり、キャンセル例外で完了済み区間の行まで破棄される。それらは `SegmentTranscribed` で画面に出ているので `.txt` と画面が食い違う。T112 以前からある問題だが、1 チャンクが複数区間に分かれるようになり影響範囲が広がった |

`docs/adr/0001-baseline-architecture.md` にも `IsSilent` への言及が残るが、**ADR は過去の決定の記録なので書き換えない**。

## 10. 前提

1. **whisper.cpp の推論コストは入力長にほぼ依存しない**（mel を 30 秒窓にパディングしてエンコードするため）。
   したがって区間数がそのままコストになる。D2 と D6 はこの前提に立っている。崩れる場合、
   結合幅 2.0 秒と非分割率 90% の妥当性を見直す必要がある。
2. **`settings.json` は利用者が手で編集しうる。** だからこそ B3 のクランプが要る。
3. **`SilenceCut` はセッション開始前に 1 度だけ設定される。** 録音中に変更する経路は作らないため、
   `TranscriptionLoop` スレッドとの競合は起きない。

---

## 実行結果 (2026-08-20)

- `dotnet build AudioCaptureApp.slnx -c Debug` : 警告 **0** 件 / エラー **0** 件
- `dotnet format AudioCaptureApp.slnx --verify-no-changes`: 差分 **なし**（exit 0）
- `dotnet test AudioCaptureApp.slnx -c Debug` : **92** 件成功 / **0** 件失敗 / **0** 件スキップ
  - 内訳: 着手前 75 件 → `SilenceCutOptions_*` 5 件・`SplitVoicedRegions_*` 19 件を追加、
    `IsSilent_*` 11 件を削除（振る舞いは `SplitVoicedRegions_*` が引き継ぐ）
  - `AppSettings` の 3 テストは新規追加ではなく既存テストへアサーションを追加（件数は変わらない）
  - 最終レビュー後に `RegionStart_*` 4 件を追加（88 → 92）

### 計画からの逸脱

| # | 逸脱 | 理由 |
|---|---|---|
| 1 | `docs/spec/02_architecture.md` と `docs/harness/20-architecture-standards.md` を変更した | どちらも `internal static` ヘルパーの例として `IsSilent` を挙げており、削除で事実誤りになるため。当初「02 は影響なし」としていたのは誤り |
| 2 | `docs/spec/04_sequence_diagram.md` の「6. ファイルからの文字起こし」も書き換えた | 計画は §3 だけを対象にしていたが、§6 にも `IsSilent` の参照が残っていた |
| 3 | `docs/spec/01_requirements.md` の REQ-TRX-08 と REQ-CFG-01 も改訂した | REQ-TRX-08 が「チャンク」基準のままだと、区間へ `PadToMinimum` を適用することが仕様から読み取れない |
| 4 | `SilenceCutOptions` を `readonly record struct` から `sealed record`（クラス）へ変更 | 構造体だと `default(SilenceCutOptions)` がクランプを丸ごと迂回し、閾値 0 ＝「全窓が有声」で機能が黙って無効化されるため |
| 5 | `MergeCloseRegions` に `Math.Max`、`gapThreshold` への改名、`TouchingGap` 定数を追加 | 「区間は整列済みかつ非入れ子」という暗黙の前提に依存していたのを解消 |
| 6 | 計画の 13 件に加えて `SplitVoicedRegions_*` を 6 件追加（計 19 件） | ミューテーションテストで、結合境界（`<` を `<=` にしても通る）・パディング後の再結合（削除しても通る）・90% 境界・窓長の倍数でないチャンク長が**どれもテストで守られていない**ことが判明したため |
| 7 | T125 / T126 を起票 | 実装中に見つけた別問題。本タスクでは直さない（法 1） |

### 手動確認をお願いしたいこと

ハルシネーション抑止の効果はユニットテストで測れない（モデルと実音声が要る）。

1. 長い無音区間を含む録音で **「ご視聴ありがとうございました」等が出力されない**こと
2. 無音を挟んだ 2 つの発話が、**それぞれ実時刻どおりに**記録されること（時刻がずれていないこと）
3. 短い発話・小さめの声が**引き続き取りこぼされない**こと（T116 の成果を後退させていないこと）
4. 連続発話中に**文字起こしが遅延しない**こと（推論回数が増えていないこと）
5. 音声ファイル文字起こしでも 1〜4 と同じ改善が得られること
