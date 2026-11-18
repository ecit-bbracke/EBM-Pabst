using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentRagSystem.Core.Services;

/// <summary>
/// Service responsible for resolving non-standard or colloquial fan dimensions and terms
/// (e.g. 120mm, 121mm, 118mm, "computerblæser") into official ebm-papst standard dimensions
/// and catalog search terms.
/// </summary>
public static class DimensionalQueryExpander
{
    /// <summary>
    /// Official ebm-papst standardized compact and industrial fan dimensions in mm.
    /// </summary>
    public static readonly int[] KnownEbmDimensions = new[]
    {
        25, 40, 50, 60, 70, 80, 92, 119, 127, 140, 150, 160, 172, 175, 180, 190, 200, 206, 220, 225, 250, 280, 300, 310, 315, 355, 385, 400, 450, 500, 560, 630, 710, 800, 910, 990
    };

    private static readonly HashSet<int> KnownEbmDimensionSet = new(KnownEbmDimensions);

    // Regex for 120-class compact fan dimensions (117 to 123 mm, 120x120, 121x121, etc.)
    private static readonly Regex Dimension120ClassRegex = new(
        @"\b(?:(?:11[7-9]|12[0-3])\s*x\s*(?:11[7-9]|12[0-3])(?:\s*x\s*\d{2})?(?:\s*mm)?|(?:Ø\s*|\b)(?:11[7-9]|12[0-3])\s*mm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Regex for colloquial computer and cabinet fan terms
    private static readonly Regex ComputerFanColloquialRegex = new(
        @"\b(?:computer\s*blæser[e]?|computer\s*ventilator[er]?|computer\s*fan[s]?|pc\s*blæser[e]?|pc\s*fan[s]?|kabinet\s*blæser[e]?|kabinet\s*ventilator[er]?|chassis\s*fan[s]?|rack\s*blæser[e]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Regex for 80-class compact fan dimensions (78 to 83 mm, 80x80)
    private static readonly Regex Dimension80ClassRegex = new(
        @"\b(?:(?:7[8-9]|8[0-3])\s*x\s*(?:7[8-9]|8[0-3])(?:\s*x\s*\d{2})?(?:\s*mm)?|(?:Ø\s*|\b)(?:7[8-9]|8[0-3])\s*mm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Regex for 92-class compact fan dimensions (88 to 95 mm, 90x90, 92x92)
    private static readonly Regex Dimension92ClassRegex = new(
        @"\b(?:(?:8[8-9]|9[0-5])\s*x\s*(?:8[8-9]|9[0-5])(?:\s*x\s*\d{2})?(?:\s*mm)?|(?:Ø\s*|\b)(?:8[8-9]|9[0-5])\s*mm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Regex for 140-class compact fan dimensions (138 to 143 mm, 140x140)
    private static readonly Regex Dimension140ClassRegex = new(
        @"\b(?:(?:13[8-9]|14[0-3])\s*x\s*(?:13[8-9]|14[0-3])(?:\s*x\s*\d{2})?(?:\s*mm)?|(?:Ø\s*|\b)(?:13[8-9]|14[0-3])\s*mm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Resolves a requested dimension to the nearest official ebm-papst standard dimension within tolerance.
    /// E.g. 120 or 121 mm -> 119 mm (standard 119x119 mm compact fan format).
    /// </summary>
    /// <param name="requestedMm">The dimension in mm requested by the user.</param>
    /// <param name="maxToleranceMm">Maximum allowed deviation in mm (default 3 mm).</param>
    /// <returns>A tuple of (StandardSize, Delta) if a close standard exists; otherwise null.</returns>
    public static (int StandardSize, int Delta)? ResolveNearestStandardSize(int requestedMm, int maxToleranceMm = 3)
    {
        if (KnownEbmDimensionSet.Contains(requestedMm))
        {
            return (requestedMm, 0);
        }

        // Specific industrial fan standard mappings:
        // 117..123 mm maps to 119 mm (industry 120 mm class compact fans)
        if (requestedMm >= 117 && requestedMm <= 123)
        {
            return (119, requestedMm - 119);
        }

        // 88..95 mm maps to 92 mm
        if (requestedMm >= 88 && requestedMm <= 95)
        {
            return (92, requestedMm - 92);
        }

        // 78..83 mm maps to 80 mm
        if (requestedMm >= 78 && requestedMm <= 83)
        {
            return (80, requestedMm - 80);
        }

        // 138..143 mm maps to 140 mm
        if (requestedMm >= 138 && requestedMm <= 143)
        {
            return (140, requestedMm - 140);
        }

        // Generic closest search within maxToleranceMm
        int closest = KnownEbmDimensions[0];
        int minDiff = Math.Abs(requestedMm - closest);

        foreach (var d in KnownEbmDimensions)
        {
            int diff = Math.Abs(requestedMm - d);
            if (diff < minDiff)
            {
                minDiff = diff;
                closest = d;
            }
        }

        if (minDiff <= maxToleranceMm)
        {
            return (closest, requestedMm - closest);
        }

        return null;
    }

    /// <summary>
    /// Expands queries containing colloquial or approximate dimensions (such as 120mm, 121mm, "computerblæser")
    /// with official ebm-papst terms (e.g. "119 mm", "119x119", "compact fan", "ACi 4400", "4400").
    /// </summary>
    public static string Expand(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var sb = new StringBuilder(query.Trim());
        bool expanded120 = false;

        // 1. Check for 120-class dimensions (118, 120, 121 mm etc.)
        if (Dimension120ClassRegex.IsMatch(query))
        {
            sb.Append(" \"119 mm\" \"119x119\" \"119 x 119\" \"compact fan\" \"Kompaktlüfter\" \"axial compact\" \"4400\" \"4300\" \"ACi 4400\"");
            expanded120 = true;
        }

        // 2. Check for colloquial computer fan terms
        if (ComputerFanColloquialRegex.IsMatch(query))
        {
            if (!expanded120)
            {
                sb.Append(" \"119 mm\" \"119x119\" \"compact fan\" \"Kompaktlüfter\" \"axial compact\" \"4400\" \"4300\"");
                expanded120 = true;
            }
            else
            {
                sb.Append(" \"compact fan\" \"Kompaktlüfter\"");
            }
        }

        // 3. Check for 80-class dimensions
        if (Dimension80ClassRegex.IsMatch(query))
        {
            sb.Append(" \"80 mm\" \"80x80\" \"compact fan\" \"8400\" \"8300\"");
        }

        // 4. Check for 92-class dimensions (90, 92 mm)
        if (Dimension92ClassRegex.IsMatch(query))
        {
            sb.Append(" \"92 mm\" \"92x92\" \"compact fan\" \"3200\" \"3400\"");
        }

        // 5. Check for 140-class dimensions
        if (Dimension140ClassRegex.IsMatch(query))
        {
            sb.Append(" \"140 mm\" \"140x140\" \"compact fan\" \"5100\" \"5300\"");
        }

        return sb.ToString();
    }
}
