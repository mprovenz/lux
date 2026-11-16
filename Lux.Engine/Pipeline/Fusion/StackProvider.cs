using System.Collections.Concurrent;
using Lux.Engine.Lri;

namespace Lux.Engine.Pipeline.BayerFusion;

/// <summary>
/// The stream's per-module `lt::StackFusion` results — the stacked float Bayer and the uint8 gain map that Lumen
/// returns for a stacked capture, from a map keyed by camera id that the stream fills lazily on first use. One shared, lazily
/// computed fusion per module, so the reference cache (`ReferenceImageCache::processLevel`), the level-1 color fusion, the mono
/// fusion (`MonoFusion::initialize`), the fusion render's STD plane (`FusionCacheBayer::render`) and the telephoto level-0 caches
/// (`SourceImageCache::lambda`) all consume the same frame, as in Lumen where the stream owns it.
/// <para><b>Frame 0's black</b> follows the load order: the stack driver estimates the black of every frame IT decodes
/// (neutral, 42.0, 1.2, 40), but a frame decoded earlier by another path keeps the sensor DB black. The reference module's
/// frame 0 is always loaded through the estimating stream loader; a non-reference module's frame 0 is decoded without an estimate by the registration /
/// tele decoder in the level-0 (ResAmp) flow, and by nobody before the fusion in a level-1 render — the same rule
/// as <see cref="PackedBayerFusion.SourceFrameBlackEstimate"/>. Frames 1..N−1 are only ever decoded by the stack driver,
/// so they always carry the estimate.</para>
/// </summary>
public sealed class StackProvider
{
    readonly LriFile _lri; readonly float _cct, _tint; readonly Action<string>? _log;
    readonly ConcurrentDictionary<string, Lazy<StackFusion>> _map = new();

    public string ReferenceModule { get; }
    /// <summary>Frame-0 black rule for the non-reference modules: `true` = per-frame estimate (level-1 flow), `false` = sensor DB black (level-0 flow).
    /// CLI override `LUX_FUSION_SRC_BLACK=db|estimate` (shared with the color fusion's single-frame sources).</summary>
    public bool SourceFrameBlackEstimate { get; }
    public bool IsStacked => _lri.StackFrames >= 2;

    /// <summary>Modules whose frame 0 carries the estimate whatever <see cref="SourceFrameBlackEstimate"/> says (a session render's
    /// reference-group sources, <see cref="Session.BuildOptions.SessionRender"/>); null = none.</summary>
    public IReadOnlySet<string>? EstimateModules { get; }

    public StackProvider(LriFile lri, float cct, float tint, Action<string>? log, bool sourceFrameBlackEstimate, string? referenceModule = null, IReadOnlySet<string>? estimateModules = null)
    {
        _lri = lri; _cct = cct; _tint = tint; _log = log; EstimateModules = estimateModules;
        ReferenceModule = referenceModule ?? lri.ReferenceModule;
        SourceFrameBlackEstimate = Environment.GetEnvironmentVariable("LUX_FUSION_SRC_BLACK") switch { "db" => false, "estimate" => true, _ => sourceFrameBlackEstimate };
    }

    /// <summary>The module's fusion on a stacked capture, computed once (thread-safe).</summary>
    public StackFusion Get(string moduleName)
        => _map.GetOrAdd(moduleName, n => new Lazy<StackFusion>(
               () => new StackFusion(_lri, n, _cct, _tint, _log, 0, refFrameBlackEstimate: n == ReferenceModule || SourceFrameBlackEstimate || (EstimateModules?.Contains(n) ?? false)),
               LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Null for a single-frame capture (Lumen returns null unless the stack has more than one frame).</summary>
    public StackFusion? GetIfStacked(string moduleName) => IsStacked ? Get(moduleName) : null;
}
