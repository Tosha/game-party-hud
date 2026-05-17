using System;
using GamePartyHud.Capture;

namespace GamePartyHud.Bars;

/// <summary>
/// Soft-guidance validator for a user-selected bar region. Returns a single
/// <see cref="ValidationResult"/> — the most actionable issue, or
/// <see cref="ValidationLevel.Ok"/> if nothing is amiss. Never blocks save;
/// the caller surfaces the result as a coloured status icon + tooltip.
///
/// Rules, applied in order. (Narrow is checked before tall because a
/// small region — say 40x22 — is both narrow by absolute width AND tall
/// by aspect ratio; "narrow" is the more actionable diagnosis when both
/// trigger.)
///   1. Empty region                  → Error
///   2. No saturated columns          → Error
///   3. Region too narrow             → Warning
///   4. Region too tall               → Warning
///   5. Low fill at pick time         → Warning  (isPickTime only)
///   6. Fragmented fill (horizontal)  → Warning
///   7. Vertically stacked bars       → Warning
///   8. All checks pass               → Ok
/// </summary>
public static class BarRegionValidator
{
    // Geometry thresholds. Tuned to the empirically-observed bar sizes
    // (~250–300 wide × 18–24 tall in typical MMO HUDs). Numbers outside
    // these envelopes almost always mean the user grabbed too much or
    // too little.
    public const int MaxReasonableHeight = 30;
    public const int MinReasonableWidth = 60;
    public const float MinFillAtPickTime = 0.85f;

    // Threshold for the vertical-fragmentation rule: if at least this
    // fraction of columns have >=2 distinct saturated runs, the region
    // is treated as multiple stacked bars (or a strong horizontal
    // discontinuity). 0.60 catches the user's "two stacked bars" case
    // (~100% of columns have 2 runs) without false-positives on text
    // overlays or single-bar gradients (<20% of columns).
    public const double MultiBarColumnFraction = 0.60;

    public static ValidationResult Validate(
        CaptureRegion region,
        ReadOnlySpan<byte> bgra,
        bool isPickTime)
    {
        // Rule 1: empty region.
        if (region.W <= 0 || region.H <= 0)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "Region is empty. Click Re-pick and drag a box around the bar.");
        }

        // We assume LTR for the validator (the only direction the picker emits today).
        var cal = new BarCalibration(region, FillDirection.LTR);

        // Build the column-filled array using the same saturation/run-length
        // signal the production analyzer uses. Empty span happens only if
        // bgra is shorter than expected — treat as "no saturated pixels".
        bool[] colFilled;
        try
        {
            colFilled = BarAnalyzer.ClassifyColumns(bgra, region.W, region.H, cal);
        }
        catch (ArgumentException)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "Region is empty. Click Re-pick and drag a box around the bar.");
        }

        int filledCount = 0;
        for (int i = 0; i < colFilled.Length; i++) if (colFilled[i]) filledCount++;

        // Rule 2: nothing saturated at all → not a bar.
        if (filledCount == 0)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "No colored bar pixels detected. Try dragging a tight box around the colored bar itself — not the background or frame.");
        }

        // Rule 3: region too narrow.
        if (region.W < MinReasonableWidth)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Box looks narrow ({region.W}px). For best accuracy, drag across the full visible width of the bar.");
        }

        // Rule 4: region too tall.
        if (region.H > MaxReasonableHeight || region.H * 5 > region.W)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Box looks too tall ({region.H}px tall vs {region.W}px wide). Try dragging just the bar's height — no frame above or below.");
        }

        // Compute fill fraction for both the pick-time rule and the OK message.
        // We count the leading run of filled columns from the anchor side
        // (the same direction Analyze uses) divided by total width.
        int leadingFilledRun = 0;
        for (int i = 0; i < colFilled.Length && colFilled[i]; i++) leadingFilledRun++;
        float fillFraction = (float)leadingFilledRun / region.W;

        // Rule 5: bar wasn't full when picked (only at pick time).
        if (isPickTime && fillFraction < MinFillAtPickTime)
        {
            int pct = (int)Math.Round(fillFraction * 100f);
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Bar was only {pct}% full when picked. For best calibration, have HP / Stamina / Mana at maximum before clicking Pick.");
        }

        // Rule 6: fragmented fill — more than one contiguous run of filled
        // columns separated by empties. A clean bar has at most one filled
        // run followed by one empty run.
        int filledRuns = 0;
        bool inRun = false;
        for (int i = 0; i < colFilled.Length; i++)
        {
            if (colFilled[i] && !inRun) { filledRuns++; inRun = true; }
            else if (!colFilled[i]) inRun = false;
            if (filledRuns >= 2) break;
        }
        if (filledRuns >= 2)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                "The captured pixels don't look like one continuous bar. Did the box catch part of an adjacent bar or icon?");
        }

        // Rule 7: vertically stacked bars / strong horizontal break.
        // For each column count distinct saturated runs (each at least
        // height/10 tall, with a floor of 2 px so 1-2 px noise doesn't
        // count). A clean bar has exactly 1 run per column; two stacked
        // bars produce 2 runs in every column. Text overlays produce
        // 2 runs only in the few columns under text characters, so the
        // 60 %-of-columns threshold doesn't fire there.
        int minRunPx = Math.Max(2, region.H / 10);
        int[] runCounts = BarAnalyzer.CountColumnSaturatedRuns(bgra, region.W, region.H, cal, minRunPx);
        int multiRunColumns = 0;
        for (int i = 0; i < runCounts.Length; i++)
        {
            if (runCounts[i] >= 2) multiRunColumns++;
        }
        if ((double)multiRunColumns / region.W >= MultiBarColumnFraction)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                "Region looks like multiple bars stacked vertically or has a strong horizontal break. Try picking just one bar.");
        }

        // Rule 8: all clear.
        int okPct = (int)Math.Round(fillFraction * 100f);
        return new ValidationResult(
            ValidationLevel.Ok,
            $"Looks good. Detected {region.W}×{region.H} at ({region.X}, {region.Y}), {okPct}% fill.");
    }
}
