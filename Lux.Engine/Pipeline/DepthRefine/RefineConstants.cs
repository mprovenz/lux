namespace Lux.Engine.Pipeline.DepthRefine;

/// <summary>
/// Every threshold of the depth refinement, in one place. Distances in pixels refer to the grid of the depth cache. Disparities are
/// pixels of shift between the two reference-group cameras that are farthest apart, on the same grid.
/// </summary>
public static class RefineConstants
{
    /// <summary>The version of the rules below. Change it whenever a value or a rule changes: stored refined depth records it.</summary>
    public const int RulesVersion = 3;

    // ---- the network passes
    /// <summary>The shorter side of the whole-picture pass, in pixels. The network was trained at this size.</summary>
    public const int NetworkSide = 518;
    /// <summary>The network reads the picture in patches of this size, so every input side is a multiple of it.</summary>
    public const int NetworkPatch = 14;
    /// <summary>The side of one tile of the picture, in pixels. Each tile runs at <see cref="NetworkSide"/>.</summary>
    public const int TileSide = 1036;
    /// <summary>The part of a tile that the next tile covers again.</summary>
    public const double TileOverlap = 0.25;
    /// <summary>Tiles are compared with the whole-picture pass on every n-th pixel.</summary>
    public const int TileFitStep = 4;
    /// <summary>Below this scale the tiled map takes its values from the whole-picture pass (Gaussian sigma, pixels).</summary>
    public const double LowPassSigma = 96.0;
    /// <summary>The low-pass filter runs on a grid reduced by this factor.</summary>
    public const int LowPassReduction = 8;
    /// <summary>The floor of the feather window of a tile.</summary>
    public const float TileWindowFloor = 1e-3f;
    /// <summary>Show the network the picture as the camera was held. A capture held turned is stored sideways or upside down. With this
    /// switch on, the picture is turned upright for the network and the network's maps are turned back. A capture held level is not
    /// affected.</summary>
    public const bool NetworkUpright = false;
    public static readonly float[] NetworkMean = { 0.485f, 0.456f, 0.406f };
    public static readonly float[] NetworkStd = { 0.229f, 0.224f, 0.225f };

    // ---- depth steps and trusted camera depth
    /// <summary>A depth step is a disparity difference above max(<see cref="StepPixels"/>, <see cref="StepRelative"/> × disparity).</summary>
    public const float StepPixels = 4.0f, StepRelative = 0.08f;
    /// <summary>The camera depth is not trusted within this distance of a depth step on the near side of the step.</summary>
    public const int NearSideReach = 24;
    /// <summary>The camera depth is not trusted within this distance of a depth step on the far side of the step.</summary>
    public const int FarSideReach = 3;
    /// <summary>The camera depth is trusted only where the picture has texture: a local standard deviation of the brightness above
    /// <see cref="TextureMinDeviation"/> gray levels in a window of <see cref="TextureWindow"/> pixels.</summary>
    public const int TextureWindow = 15;
    public const float TextureMinDeviation = 1.0f;

    // ---- the map from network values to disparity
    /// <summary>The curve uses every n-th trusted pixel and this number of bins of equal count.</summary>
    public const int CurveStep = 3, CurveBins = 64;
    /// <summary>With fewer trusted samples than this only the sky of the capture is corrected.</summary>
    public const int CurveMinSamples = 4096;
    /// <summary>The local shift of the curve: the cell size, the sampling step and the fewest trusted samples of a cell.</summary>
    public const int ShiftCell = 384, ShiftStep = 2, ShiftMinSamples = 200;

    // ---- the other cameras of the reference group (the cross-view cost)
    /// <summary>The fewest other cameras of the reference group that the cross-view cost needs.</summary>
    public const int CrossViewMinCameras = 2;
    /// <summary>The largest number of cameras of the group that are rendered at the same time. Two at a time is faster and needs
    /// about 2 GB more memory than one at a time. The result is the same.</summary>
    public const int CrossViewCamerasAtOnce = 2;
    /// <summary>A computer with less memory than this (GB) renders the cameras of the group one at a time.</summary>
    public const int CrossViewTwoAtOnceMinMemoryGb = 24;

    /// <summary>The number of cameras of the group to render at the same time on this computer.</summary>
    public static int CrossViewCamerasNow(int threads)
    {
        long totalGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 30;
        int most = totalGb < CrossViewTwoAtOnceMinMemoryGb ? 1 : CrossViewCamerasAtOnce;
        return Math.Clamp(threads, 1, most);
    }
    /// <summary>The cost compares the brightness after a Gaussian blur of this sigma (pixels), minus its local mean: a difference of
    /// exposure or shading between two cameras then does not count.</summary>
    public const double CrossViewBlurSigma = 0.8;
    /// <summary>The side of the square window of that local mean (pixels).</summary>
    public const int CrossViewMeanWindow = 15;
    /// <summary>The absolute difference to one camera is averaged over a square window of this side (pixels).</summary>
    public const int CrossViewWindow = 5;
    /// <summary>The cost of a pixel is the mean over this number of cameras with the lowest difference: a camera that does not see the
    /// pixel then does not count.</summary>
    public const int CrossViewBestCameras = 2;
    /// <summary>The difference to a camera that has no picture at the pixel (gray levels).</summary>
    public const float CrossViewNoPicture = 40.0f;
    /// <summary>A cost at or above this value means that fewer than <see cref="CrossViewBestCameras"/> cameras have a picture there.</summary>
    public const float CrossViewKnownBelow = 39.9f;
    /// <summary>The residual shift of each camera is measured on a grid of this many cells.</summary>
    public const int AlignCellsX = 8, AlignCellsY = 6;
    /// <summary>The shift search of a cell covers this many pixels to each side.</summary>
    public const int AlignSearch = 6;
    /// <summary>A cell uses at most <see cref="AlignSamples"/> pixels with texture, and needs <see cref="AlignMinSamples"/>.</summary>
    public const int AlignSamples = 6000, AlignMinSamples = 800;
    /// <summary>A pixel has texture for the shift search when the sum of its two absolute brightness gradients is above this.</summary>
    public const float AlignMinGradient = 24.0f;
    /// <summary>No sample is taken within this distance of the picture border.</summary>
    public const int AlignBorder = 16;
    /// <summary>The shift search limits each absolute difference to this value (gray levels).</summary>
    public const float AlignMaxDifference = 20.0f;
    /// <summary>A cell's shift counts only when its lowest mean difference is below this part of the median over all shifts.</summary>
    public const double AlignMinGain = 0.85;

    /// <summary>The camera depth is trusted only where the cross-view cost is at most
    /// max(<see cref="CostLimitMin"/>, <see cref="CostLimitScene"/> × the median cost of the picture's textured pixels).</summary>
    public const float CostLimitMin = 4.0f, CostLimitScene = 1.4f;
    /// <summary>The median cost of the picture is taken from every n-th pixel of every n-th row.</summary>
    public const int CostMedianStep = 2;
    /// <summary>The other cameras confirm the camera depth where the picture has texture and the cost is at most this part of the limit
    /// above. Confirmed depth is never rewritten outside the sky.</summary>
    public const float ConfirmedCostPart = 0.7f;

    // ---- the sky
    /// <summary>A sky candidate has a whole-picture network value below this part of the network's near value.</summary>
    public const float SkyNetworkMax = 0.01f;
    /// <summary>The network's near value is this percentile of its whole-picture pass.</summary>
    public const double SkyNearPercentile = 99.0;
    /// <summary>A candidate is dropped when the tiled map is nearer than the sky by more than this part of the tiled map's range.</summary>
    public const float SkyTrimRange = 0.1f;
    /// <summary>That range goes from the tiled value of the sky to this percentile of the tiled map.</summary>
    public const double SkyTrimPercentile = 99.0;
    /// <summary>A thin line of the picture is found against a Gaussian blur of the brightness with this sigma (pixels).</summary>
    public const double SkyLineSigma = 4.0;
    /// <summary>A pixel is on a line when its brightness differs from the blur by more than this (gray levels).</summary>
    public const float SkyLineContrast = 8.0f;
    /// <summary>A line is a connected group of at least this many such pixels: smaller groups are noise.</summary>
    public const int SkyLineMinPixels = 40;
    /// <summary>Lines are grown by this many pixels. A grown line is never sky.</summary>
    public const int SkyLineGrow = 2;
    /// <summary>A connected sky area is accepted only when the median camera depth on it is at least this far (mm).</summary>
    public const float SkyMinMedianDepth = 11000f;

    // ---- where the camera depth is replaced
    /// <summary>The relief of camera depth and network is compared in a window of this radius, on a grid reduced by
    /// <see cref="ReliefReduction"/>.</summary>
    public const int ReliefRadius = 48, ReliefReduction = 4;
    /// <summary>The two disagree when their positions inside the local range differ by more than this.</summary>
    public const float ReliefMaxDifference = 0.6f;
    /// <summary>Where the camera depth is flat, the two disagree when the network puts the pixel above this part of its local range.</summary>
    public const float ReliefFlatMaxPosition = 0.5f;
    /// <summary>Camera depth and mapped network agree within max(<see cref="AgreePixels"/>, <see cref="AgreeRelative"/> × disparity).</summary>
    public const float AgreePixels = 4.0f, AgreeRelative = 0.15f;

    // ---- the values written there
    /// <summary>The joint filter: the range kernel on the logarithm of the mapped network disparity, the spatial sigma on the grid reduced
    /// by <see cref="FilterReduction"/>, and the support below which the mapped network value takes over.</summary>
    public const float FilterRangeSigma = 0.08f, FilterSpatialSigma = 4.0f, FilterMinSupport = 0.05f;
    public const int FilterReduction = 4;
    /// <summary>The smallest disparity the logarithm sees.</summary>
    public const float MinLogDisparity = 0.05f;
    public const double FilterLowPercentile = 0.1, FilterHighPercentile = 99.9;

    /// <summary>The baseline (mm) that stands in when the capture has no reference group of wide cameras.</summary>
    public const double FallbackBaseline = 40.0;
    /// <summary>The far limit of the depth cache (mm). The sky is written at this depth.</summary>
    public const float FarDepth = 100000f;
}
