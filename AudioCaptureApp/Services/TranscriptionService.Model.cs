using System.Globalization;
using System.IO;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、Whisper モデルの読み込み（REQ-TRX-02 / 03、REQ-GPU-05 / 07）を担当する部分。
// 言語の指定・GPU の実態の判定（ネイティブログの解析。T123）・flash attention・破棄を含む（T193 で切り出した）。
public partial class TranscriptionService
{
    /// <summary>
    /// 言語の指定を <see cref="WhisperProcessorBuilder"/> へ載せる（REQ-TRX-10）。
    /// 自動判定は言語コードではなく専用の API で有効化する。
    /// </summary>
    private static WhisperProcessorBuilder WithLanguageOption(WhisperProcessorBuilder builder, string language)
        => string.Equals(language, TranscriptionLanguages.Auto, StringComparison.OrdinalIgnoreCase)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);

    // GPU優先順（CUDA > Vulkan > CoreML > OpenVino > CPU）
    private static readonly List<RuntimeLibrary> GpuPreferredOrder = new()
    {
        RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.CoreML,
        RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx
    };

    private static bool IsGpuLibrary(RuntimeLibrary? library) =>
        library is RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

    /// <summary>
    /// ステータス通知用の実行先表記を作る（REQ-GPU-05）。
    /// GPU 実行のときだけランタイム種別を併記する。
    /// </summary>
    internal static string DescribeRuntime(RuntimeLibrary? loaded, bool gpuInUse)
        => gpuInUse && IsGpuLibrary(loaded) ? $"GPU ({loaded})" : "CPU";

    // --- モデル読み込み中のネイティブログから GPU の実態を読み取る (T123) ---
    //
    // GPU 版のランタイム DLL は使える GPU デバイスが無くても読み込めるため、
    // RuntimeOptions.LoadedLibrary だけでは GPU 利用可否を判定できない
    // （Vulkan ローダーの ICD を無効化した実測で、CPU 速度なのに "GPU (Vulkan)" と
    //  表示されることを確認済み）。Whisper.net には他に GPU の情報源が無いため、
    // whisper.cpp / ggml がモデル読み込み時に出すログから 2 つの事実を拾う。
    //
    //   whisper_init_with_params_no_state: backends   = 2   → GPU バックエンドが登録されたか
    //   whisper_model_load:      Vulkan0 total size = ...    → 重みが実際にどこへ載ったか
    //
    // ログ書式は公開 API ではないため、解析できなければ従来の判定へフォールバックする。

    /// <summary>
    /// <c>whisper_init_with_params_no_state: backends   = 2</c> の形の行から数値を取り出す。
    /// 該当しない行なら <c>null</c>。
    /// </summary>
    internal static int? ParseBackendCount(string logLine)
    {
        const string key = "backends";
        int keyIndex = logLine.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        int equalsIndex = logLine.IndexOf('=', keyIndex + key.Length);
        if (equalsIndex < 0)
        {
            return null;
        }

        return int.TryParse(
            logLine.AsSpan(equalsIndex + 1).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var count) ? count : null;
    }

    /// <summary>
    /// <c>whisper_model_load:      Vulkan0 total size =   487.01 MB</c> の形の行から
    /// 重みが載ったバックエンド名（<c>Vulkan0</c> / <c>CPU</c> 等）を取り出す。
    /// 該当しない行なら <c>null</c>。
    /// </summary>
    internal static string? ParseModelBackend(string logLine)
    {
        const string marker = "total size";
        if (!logLine.Contains("whisper_model_load:", StringComparison.Ordinal))
        {
            return null;
        }

        int markerIndex = logLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        // "total size" の直前のトークンがバックエンド名
        var head = logLine.AsSpan(0, markerIndex).TrimEnd();
        int separator = head.LastIndexOf(' ');
        if (separator < 0)
        {
            return null;
        }

        var name = head[(separator + 1)..].ToString();
        // バックエンド名が無い行（"whisper_model_load: total size ..."）を拾わない
        return name.Length == 0 || name.EndsWith(':') ? null : name;
    }

    /// <summary>重みの載ったバックエンド名から GPU 実行かどうかを決める。</summary>
    internal static bool IsGpuInUse(string? modelBackend)
        => modelBackend != null && !modelBackend.Equals("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// モデル読み込み中のネイティブログを覗いて、GPU の実態を拾う。
    /// 購読は <see cref="LoadModel"/> の読み込み区間だけに限る。
    /// </summary>
    private sealed class NativeLoadObserver
    {
        private int? _backendCount;
        private string? _modelBackend;

        public void OnLog(WhisperLogLevel level, string? message)
        {
            _ = level;
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            _backendCount ??= ParseBackendCount(message);
            _modelBackend ??= ParseModelBackend(message);
        }

        /// <summary>GPU バックエンドが登録されたか。解析できなければ <c>null</c>（不明）。</summary>
        public bool? HasGpuBackend => _backendCount is int count ? count >= 2 : null;

        /// <summary>いま GPU で動いているか。解析できなければ <c>null</c>（不明）。</summary>
        public bool? GpuInUse => _modelBackend != null ? IsGpuInUse(_modelBackend) : null;
    }

    // GPU利用可否は実際にランタイムを読み込んでみないと判定できないため、GPU 優先順で読み込む。
    //
    // ここで RuntimeLibraryOrder が効くのは「プロセス内で最初の読み込み」だけである（REQ-TRX-02）。
    // Whisper.net は WhisperFactory.LibraryLoaded を static な Lazy<LoadResult> で持ち、
    // ネイティブランタイムをプロセスで 1 度しか読み込まない。Factory を破棄しても
    // アンロードされないため、順序を CPU 限定に差し替えて読み込み直しても空振りする（T119）。
    // したがって CPU 実行への切り替えは WhisperFactoryOptions.UseGpu で行う（REQ-TRX-03）。
    public (bool Success, bool GpuAvailable) LoadModel(string modelPath, bool useGpu)
    {
        DisposeProcessor();

        if (!File.Exists(modelPath))
        {
            return (false, true);
        }

        try
        {
            RuntimeOptions.RuntimeLibraryOrder = GpuPreferredOrder;

            var observer = new NativeLoadObserver();
            bool flash = ShouldUseFlashAttention(UseFlashAttentionOnGpu, useGpu);
            using (LogProvider.AddLogger(observer.OnLog))
            {
                _factory = WhisperFactory.FromPath(
                    modelPath, new WhisperFactoryOptions { UseGpu = useGpu, UseFlashAttention = flash });

                // FromPath はモデルの読み込みをその場で行うが、失敗しても例外を投げずに
                // ファクトリを返す。失敗が表面化するのは最初の CreateBuilder() なので、
                // ここで 1 度呼んで確定させる（読み込み済みのため追加コストは無い。実測 0ms）。
                // これが無いと壊れたモデルでも「読み込み完了」と表示され、録音開始や
                // ファイル文字起こしまで失敗が判明しない（T122）。
                _ = _factory.CreateBuilder();
            }
            _loadedModelPath = modelPath;

            var loaded = RuntimeOptions.LoadedLibrary;

            // GPU 版ランタイムを読み込めただけでは GPU が使えるとは限らない（T123）。
            // ログを解析できなかった場合は従来どおりランタイム種別だけで判定する。
            var gpuAvailable = IsGpuLibrary(loaded) && observer.HasGpuBackend != false;
            var gpuInUse = observer.GpuInUse ?? (useGpu && gpuAvailable);

            // REQ-GPU-07: GPU を頼んだのに重みが CPU に載ったら、flash attention 無しで読み込み直す（CPU では遅くなるため）
            if (NeedsReloadWithoutFlash(flash, observer.GpuInUse))
            {
                // 先に手放す（読み込み直しが例外になったとき、下の catch が破棄済みの factory を二重に破棄しないように）
                var withFlash = _factory;
                _factory = null;
                withFlash.Dispose();
                _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
                _ = _factory.CreateBuilder();
            }

            RuntimeInfo?.Invoke(DescribeRuntime(loaded, gpuInUse));
            return (true, gpuAvailable);
        }
        // CA1031: Whisper のネイティブランタイム読み込みは DllNotFoundException 等、
        //         環境依存の任意の例外を投げる。失敗は Error イベントに変換して継続する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            Error?.Invoke($"Whisperモデル読み込み失敗: {ex.Message}");
            DisposeProcessor();
            return (false, true);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// GPU で動くときに flash attention を使うか（REQ-GPU-07 / REQ-CFG-14）。MainViewModel が設定から反映する。
    /// 読まれるのはモデルの読み込み（<see cref="LoadModel"/> とファイル文字起こしの 2 つ目のモデル）のときだけ。
    /// </summary>
    public bool UseFlashAttentionOnGpu { get; set; } = true;

    /// <summary>
    /// flash attention を使うか（REQ-GPU-07）。有効で、かつ GPU で読み込むときだけ使う。
    /// CPU では遅くなる（small で +23%。T191 の実測）。
    /// </summary>
    internal static bool ShouldUseFlashAttention(bool enabled, bool useGpu) => enabled && useGpu;

    /// <summary>
    /// flash attention を使って読み込んだのに、重みが CPU に載ったか（REQ-GPU-07）。そのときは使わずに読み込み直す。
    /// 載った先が分からない（ネイティブログを解析できなかった）ときは読み込み直さない。
    /// </summary>
    internal static bool NeedsReloadWithoutFlash(bool flashUsed, bool? gpuInUse) => flashUsed && gpuInUse == false;

    /// <summary>
    /// 読み込み済みのモデルを破棄して「未読み込み」へ戻す（REQ-MODELWIN-04）。
    /// 選択中のモデルが一覧から削除されたときに使う。セッション停止後にのみ呼ぶこと。
    /// </summary>
    public void UnloadModel() => DisposeProcessor();

    private void DisposeProcessor()
    {
        foreach (var state in _sources.Values)
        {
            // ここに来る時点でセッションは停止済み（ワーカーは動いていない）
            DisposeProcessorSafely(state, workerExited: true);
        }
        lock (_sourcesLock)
        {
            _sources.Clear();
        }
        _factory?.Dispose();
        _factory = null;
        _loadedModelPath = null;
    }
}