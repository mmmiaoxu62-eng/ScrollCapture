namespace ScrollCapture.Vision;

/// <summary>
/// Per-pixel-region scroll weights produced by FixedRegionDetector and consumed
/// (optionally!) by OverlapDetector. Semantics:
///   1.0  scrolled content (normal match evidence)
///   0.0  fixed UI (must NOT influence scroll distance)
///   0.4  unknown / insufficient evidence (kept as weak evidence, never decisive)
/// Null WeightMap = "no reliable fixed detection" => the original unweighted path.
/// Defaults are scroll (1.0): an uninitialized entry can never masquerade as Fixed.
/// </summary>
public sealed record RegionWeightMap(
    double[] RowWeight,   // [frameHeight], keyed by absolute row of frame B
    double[] ColWeight,   // [frameWidth],  keyed by x
    double Confidence,    // 0..1 reliability of this classification
    string Summary)
{
    public const double FixedWeight = 0.0;
    public const double ScrollWeight = 1.0;
    public const double UnknownWeight = 0.4;

    public bool IsReliable => Confidence >= 0.5;

    /// <summary>All-scroll weight map (a "no-op" weight map — must be numerically neutral).</summary>
    public static RegionWeightMap AllScroll(int width, int height)
    {
        var row = new double[height];
        var col = new double[width];
        Array.Fill(row, ScrollWeight);
        Array.Fill(col, ScrollWeight);
        return new RegionWeightMap(row, col, 1.0, "allScroll");
    }

    public static RegionWeightMap FromRows(double[] rowWeight, int width, double confidence, string summary,
        double colDefault = ScrollWeight)
    {
        var col = new double[width];
        Array.Fill(col, colDefault);
        return new RegionWeightMap(rowWeight, col, confidence, summary);
    }

    public static RegionWeightMap FromColumns(double[] colWeight, int height, double confidence, string summary,
        double rowDefault = ScrollWeight)
    {
        var row = new double[height];
        Array.Fill(row, rowDefault);
        return new RegionWeightMap(row, colWeight, confidence, summary);
    }
}
