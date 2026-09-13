using System.Collections.Concurrent;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// The stream's per-module `lt::StackFusion` results — what `FUN_18020a6d0` (the stacked float Bayer) and `FUN_18020b870` (the uint8 gain map)
/// return for a stacked capture, from the map `stream+0x20` keyed by camera id that `FUN_18020b0b0` fills lazily on first use. One shared, lazily
/// computed fusion per module, so the reference cache (`ReferenceImageCache::processLevel`), the level-1 colour fusion (`FUN_1801f7a90`), the mono
/// fusion (`MonoFusion::initialize`), the fusion render's STD plane (`FusionCacheBayer::render`) and the telephoto level-0 caches
/// (`SourceImageCache::lambda`) all consume the same frame, as in Lumen where the stream owns it.
/// <para><b>Frame 0's black</b> (`CapturedImage+0xb4`) follows the load order: `FUN_18020b0b0` estimates the black of every frame IT decodes
/// (`FUN_180125d10(frame, neutral, 42.0, 1.2, 40)`), but a frame decoded earlier by another path keeps the sensor DB black. The reference module's
/// frame 0 is always loaded through the estimating stream loader; a non-reference module's frame 0 is decoded without an estimate by the registration /
/// tele decoder (`FUN_1804d3d00` → `FUN_180126010`) in the level-0 (ResAmp) flow, and by nobody before the fusion in a level-1 render — the same rule
/// as <see cref="PackedBayerFusion.SourceFrameBlackEstimate"/> (`a-monofusion.md` §9 item 9). Frames 1..N−1 are only ever decoded by `FUN_18020b0b0`,
/// so they always carry the estimate.</para>
/// </summary>
public sealed class StackProvider
{
    readonly LriFile _lri; readonly float _cct, _tint; readonly Action<string>? _log;
    readonly ConcurrentDictionary<string, Lazy<StackFusion>> _map = new();

    public string ReferenceModule { get; }
    /// <summary>Frame-0 black rule for the non-reference modules: `true` = per-frame estimate (level-1 flow), `false` = sensor DB black (level-0 flow).
    /// CLI override `LUX_FUSION_SRC_BLACK=db|estimate` (shared with the colour fusion's single-frame sources).</summary>
    public bool SourceFrameBlackEstimate { get; }
    public bool IsStacked => _lri.StackFrames >= 2;

    public StackProvider(LriFile lri, float cct, float tint, Action<string>? log, bool sourceFrameBlackEstimate, string? referenceModule = null)
    {
        _lri = lri; _cct = cct; _tint = tint; _log = log;
        ReferenceModule = referenceModule ?? lri.ReferenceModule;
        SourceFrameBlackEstimate = Environment.GetEnvironmentVariable("LUX_FUSION_SRC_BLACK") switch { "db" => false, "estimate" => true, _ => sourceFrameBlackEstimate };
    }

    /// <summary>`FUN_18020a6d0(stream, camId)` on a stacked capture: the module's fusion, computed once (thread-safe).</summary>
    public StackFusion Get(string moduleName)
        => _map.GetOrAdd(moduleName, n => new Lazy<StackFusion>(
               () => new StackFusion(_lri, n, _cct, _tint, _log, 0, refFrameBlackEstimate: n == ReferenceModule || SourceFrameBlackEstimate),
               LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Null for a single-frame capture (`FUN_18020a6d0` returns null unless `FUN_180112250(hdr) > 1`).</summary>
    public StackFusion? GetIfStacked(string moduleName) => IsStacked ? Get(moduleName) : null;
}
