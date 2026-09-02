using System.Windows.Media.Imaging;

namespace ScrollCapture.Vision;

/// <summary>
/// Fixed / Sticky region detection layer (runs BETWEEN capture and the existing
/// OverlapDetector; never decides overlap itself).
///
/// Per frame pair:
///   1. dy0 comes EXCLUSIVELY from the existing OverlapDetector (original path) 鈥?///      this class only treats dy0 as a prior. Invalid dy0 => null (fallback).
///   2. Horizontal strips (10 row-bands, full-res gray sampling): two-test
///        fixedSim  = sim(A[strip], B[strip])                 (same screen position)
///        scrollSim = sim(A[strip shifted by dy0], B[strip])  (moved with the page)
///      margin &gt; threshold => Fixed (0.0) / Scroll (1.0), else Unknown (0.4).
///   3. Column bands reuse ColumnMotion + content gate (sidebar => 0.0 only when
///      static AND has UI content; plain background stays 1.0).
///   4. Temporal EMA voting (per strip/band, fast-follow): sticky transitions are
///      detected dynamically and can revert when the element scrolls again.
///   5. Reliability gate: no fixed evidence / low consistency => null => the
///      original algorithm is used untouched.
/// </summary>
public sealed class FixedRegionDetector
{
    public const int RowStripCount = 10;
    public const double FixedTestMargin = 0.20;   // similarity margin for decisive classification
    public const double EmuAlpha = 0.35;          // EMA smoothing factor
    public const double MinFixConfidence = 0.5;
    public const double FixedThreshold = 0.30;    // EMA < 0.3 => fixed; EMA > 0.7 => scroll
    public const double ScrollThreshold = 0.70;

    private readonly double[] _rowEma = Enumerable.Repeat(1.0, RowStripCount).ToArray();
    private readonly double[] _colEma = Enumerable.Repeat(1.0, ColumnMotion.BandCount).ToArray();
    private readonly OverlapDetector _baseline = new();

    private double _lastDy0;
    private double _lastOverlapConfidence;

    public double LastDy0 => _lastDy0;
    public double LastOverlapConfidence => _lastOverlapConfidence;
    internal string DebugLastReport { get; private set; } = "";

    public sealed record DebugStripRecord(int StripIndex, int Y0, int Y1, double FixedSim, double ScrollSim, double Margin, string Classification, double EffectiveWeight);

    public IReadOnlyList<DebugStripRecord> LastStrips { get; private set; } = Array.Empty<DebugStripRecord>();

    /// <summary>
    /// Returns null when the original path must run untouched (no fixed evidence,
    /// low confidence, invalid dy0, or any internal error).
    /// </summary>
    public RegionWeightMap? Update(BitmapSource previous, BitmapSource current, bool[]? drivingBands)
    {
        try
        {
            if (previous.PixelWidth != current.PixelWidth || previous.PixelHeight != current.PixelHeight)
            {
                return null;
            }
            int width = previous.PixelWidth;
            int height = previous.PixelHeight;
            if (height < 200)
            {
                return null;
            }

            // (1) dy0 from the existing detector. Additional same-behavior guard used ONLY
            // INTERNALLY: rows constant at the same absolute position are excluded on the A
            // side too — fixed footers live inside A's alignment band and would otherwise
            // pollute the original score curve (the PUBLIC Detect stays byte-identical).
            // same-abs constant rows (fixed UI band detector) on the A side too
            byte[] dy0GrayA = ToGray(previous);
            byte[] dy0GrayB = ToGray(current);
            bool[] sameAbsMask = OverlapDetector.ComputeStaticMask(dy0GrayA, dy0GrayB, width, height);
            OverlapResult dy0 = _baseline.DetectWithMaskA(previous, current, priorOverlapPx: null,
                drivingBandMask: drivingBands, staticMaskA: sameAbsMask);
            if (!dy0.Success || dy0.Confidence < 0.7 || dy0.OverlapHeight <= 0 || dy0.OverlapHeight >= height)
            {
                DebugLastReport = $"baseline rejected: success={dy0.Success} conf={dy0.Confidence:F2} ov={dy0.OverlapHeight} note={dy0.Note}";
                if ((dy0.Confidence + 10) < 0) { /* keep quiet about common first-pair rejections */ }
                Utils.Logger.Info("FRD dy0 rejected: " + DebugLastReport);
                return null;
            }
            _lastDy0 = height - dy0.OverlapHeight;
            _lastOverlapConfidence = dy0.Confidence;

            byte[] grayA = ToGray(previous);
            byte[] grayB = ToGray(current);
            bool[] sameAbsMask2 = OverlapDetector.ComputeStaticMask(grayA, grayB, width, height);

            // (2) row strips: two-test classification (full-res rows, sampled columns)
            double rawFixedVotes = 0;
            double rawUnknownVotes = 0;
            int stripRows = Math.Max(1, height / RowStripCount);
            double dyFull = _lastDy0;
            var debugLines = new System.Text.StringBuilder();
            debugLines.AppendLine($"dy0={dyFull:F1} conf={dy0.Confidence:F2}");
            var stripRecords = new List<DebugStripRecord>(RowStripCount);

            for (int s = 0; s < RowStripCount; s++)
            {
                int y0 = s * stripRows;
                int y1 = Math.Min(height, y0 + stripRows);
                double fixedSim = StripSimilarity(grayA, grayB, width, height, y0, y1, 0);
                double scrollSim = StripSimilarity(grayA, grayB, width, height, y0, y1, (int)Math.Round(dyFull));

                double cls;
                if (scrollSim < 0)
                {
                    // no scroll evidence available for this strip (below the shifted A
                    // range): a HIGH same-position match means a fixed bottom element
                    cls = fixedSim >= 0.75 ? RegionWeightMap.FixedWeight : RegionWeightMap.UnknownWeight;
                    if (cls == RegionWeightMap.FixedWeight) rawFixedVotes++;
                    else rawUnknownVotes++;
                }
                else if (fixedSim - scrollSim > FixedTestMargin)
                {
                    cls = RegionWeightMap.FixedWeight;
                    rawFixedVotes++;
                }
                else if (scrollSim - fixedSim > FixedTestMargin)
                {
                    cls = RegionWeightMap.ScrollWeight;
                }
                else
                {
                    cls = RegionWeightMap.UnknownWeight;
                    rawUnknownVotes++;
                }
                _rowEma[s] = _rowEma[s] * (1 - EmuAlpha) + cls * EmuAlpha;
                debugLines.AppendLine($"s{s}: y[{y0}..{y1}) fixed={fixedSim:F2} scroll={scrollSim:F2}");
                stripRecords.Add(new DebugStripRecord(s, y0, y1, fixedSim, scrollSim,
                    Math.Abs(fixedSim - scrollSim),
                    cls == RegionWeightMap.FixedWeight ? "FIXED" : cls == RegionWeightMap.ScrollWeight ? "SCROLL" : "UNKNOWN",
                    cls));
                if (s == 1)
                {
                    long dA = 0;
                    int cn = 0;
                    for (int y = y0; y < y1; y += 2)
                    {
                        int r = y * width;
                        for (int x = 0; x < width; x += 8)
                        {
                            dA += Math.Abs(grayA[r + x] - grayB[r + x]);
                            cn++;
                        }
                    }
                    byte[] fbA = FrameSimilarity.ToBgr32Buffer(previous);
                    byte[] fbB = FrameSimilarity.ToBgr32Buffer(current);
                    int baseIdx = (45 * width + 8) * 4;
                    debugLines.AppendLine(
                        $"   s1 grayDiffAvg={dA / (double)cn:F2} bgrA=[{fbA[baseIdx]},{fbA[baseIdx + 1]},{fbA[baseIdx + 2]}] bgrB=[{fbB[baseIdx]},{fbB[baseIdx + 1]},{fbB[baseIdx + 2]}] " +
                        $"grayA[{(45 * width + 8)}]={grayA[45 * width + 8]} grayB={grayB[45 * width + 8]}");
                }
            }

            // (3) column bands: static + content => fixed sidebar; background => keep 1.0
            double[] bandMotion = ColumnMotion.ComputeBandMotion(previous, current);
            bool[] hasContent = ColumnMotion.BandHasContent(previous);
            for (int b = 0; b < _colEma.Length; b++)
            {
                double cls;
                if (bandMotion[b] >= ColumnMotion.BandDrivingThreshold)
                {
                    cls = RegionWeightMap.ScrollWeight;
                }
                else if (!hasContent[b])
                {
                    cls = RegionWeightMap.ScrollWeight; // plain background: harmless, keep
                }
                else
                {
                    cls = RegionWeightMap.FixedWeight;
                }
                _colEma[b] = _colEma[b] * (1 - EmuAlpha) + cls * EmuAlpha;
            }

            // (4) effective weighing (EMA applied)
            int fixedRows = 0;
            for (int s = 0; s < RowStripCount; s++)
            {
                if (_rowEma[s] < FixedThreshold)
                {
                    fixedRows++;
                }
            }
            if (fixedRows == 0 && rawFixedVotes == 0)
            {
                // no fixed evidence at all — keep original path
                debugLines.AppendLine("=> no-fixed-evidence: fallback");
                DebugLastReport = debugLines.ToString();
                LastStrips = stripRecords;
                return null;
            }

            var rowW = new double[height];
            Array.Fill(rowW, RegionWeightMap.ScrollWeight);
            for (int s = 0; s < RowStripCount; s++)
            {
                double eff = _rowEma[s] < FixedThreshold
                    ? RegionWeightMap.FixedWeight
                    : _rowEma[s] > ScrollThreshold
                        ? RegionWeightMap.ScrollWeight
                        : RegionWeightMap.UnknownWeight;
                int py0 = s * stripRows;
                int py1 = Math.Min(height, py0 + stripRows);
                for (int y = py0; y < py1; y++)
                {
                    rowW[y] = eff;
                }
            }

            // fixed-bottom band: rows that are constant at the same absolute position
            // AND sit inside A's alignment band (A 'aAbs' rows [k-f..k)) deserve zero
            // weight so the weighted detector no longer sees the footer mismatch.
            int footerSpan = 0;
            for (int y = height - 1; y >= 0 && sameAbsMask2[y]; y--)
            {
                footerSpan++;
            }
            if (footerSpan >= 8)
            {
                int k = Math.Max(0, height - (int)Math.Round(_lastDy0));
                int jStart = Math.Max(0, k - footerSpan - 6);
                for (int j = jStart; j < Math.Min(height, k); j++)
                {
                    rowW[j] = RegionWeightMap.FixedWeight;
                }
            }

            var colW = new double[width];
            Array.Fill(colW, RegionWeightMap.ScrollWeight);
            int bandWidth = Math.Max(1, width / ColumnMotion.BandCount);
            for (int b = 0; b < _colEma.Length; b++)
            {
                double eff = _colEma[b] < FixedThreshold
                    ? RegionWeightMap.FixedWeight
                    : _colEma[b] > ScrollThreshold
                        ? RegionWeightMap.ScrollWeight
                        : RegionWeightMap.UnknownWeight;
                for (int x = b * bandWidth; x < Math.Min(width, (b + 1) * bandWidth); x++)
                {
                    colW[x] = eff;
                }
            }

            // (5) confidence: decisive strips + fixed evidence
            double decisionRatio = (RowStripCount - rawUnknownVotes) / (double)RowStripCount;
            double confidence = Math.Clamp(0.4 + 0.12 * (decisionRatio + rawFixedVotes / (double)RowStripCount), 0.0, 1.0);
            DebugLastReport = debugLines.ToString();
            LastStrips = stripRecords;

            string summary = BuildSummary(rowW, colW);
            return new RegionWeightMap(rowW, colW, confidence, summary);
        }
        catch (Exception ex)
        {
            DebugLastReport = "EX: " + ex.Message + " @ " + (ex.StackTrace ?? "").Split('\n').FirstOrDefault();
            Utils.Logger.Info("FRD null: " + DebugLastReport);
            return null; // absolute guaranteed fallback
        }
    }

    private string BuildSummary(double[] rowW, double[] colW)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("fixedRows=");
        List<string> runs = new();
        int start = -1;
        for (int y = 0; y <= rowW.Length; y++)
        {
            bool fixedRow = y < rowW.Length && rowW[y] <= RegionWeightMap.UnknownWeight;
            if (fixedRow && start < 0) start = y;
            if (!fixedRow && start >= 0) { runs.Add($"[{start}..{y})"); start = -1; }
        }
        sb.Append(string.Join(",", runs.Count > 0 ? runs : new List<string> { "none" }));
        sb.Append(" fixedCols=");
        runs.Clear();
        start = -1;
        for (int x = 0; x <= colW.Length; x++)
        {
            bool fixedCol = x < colW.Length && colW[x] <= RegionWeightMap.UnknownWeight;
            if (fixedCol && start < 0) start = x;
            if (!fixedCol && start >= 0) { runs.Add($"[{start}..{x})"); start = -1; }
        }
        sb.Append(string.Join(",", runs.Count > 0 ? runs : new List<string> { "none" }));
        return sb.ToString();
    }

    /// <summary>Similarity of rows [y0,y1) in B against A shifted by dyFull (0 = same position).</summary>
    private static double StripSimilarity(byte[] a, byte[] b, int width, int height, int y0, int y1, int dyFull)
    {
        long total = 0;
        long count = 0;
        for (int y = y0; y < y1; y += 2)
        {
            int aY = y + dyFull;
            if (aY < 0 || aY >= height)
            {
                continue;
            }
            int bRow = y * width;
            int aRow = aY * width;
            for (int x = 0; x < width; x += 8)
            {
                total += Math.Abs(a[aRow + x] - b[bRow + x]);
                count++;
            }
        }
        if (count == 0)
        {
            return -1.0; // sentinel: NO scroll evidence (A range exhausted)
        }
        double meanDiff = total / (double)count;
        return 1.0 - Math.Clamp(meanDiff / 60.0, 0.0, 1.0);
    }

    private static byte[] ToGray(BitmapSource source)
    {
        byte[] bgr32 = FrameSimilarity.ToBgr32Buffer(source);
        var gray = new byte[bgr32.Length / 4];
        for (int i = 0, n = 0; i < bgr32.Length; i += 4, n++)
        {
            gray[n] = (byte)((bgr32[i] + bgr32[i + 1] + bgr32[i + 2]) / 3);
        }
        return gray;
    }
}


