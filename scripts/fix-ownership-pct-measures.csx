// =============================================================================
// fix-ownership-pct-measures.csx
// =============================================================================
// PURPOSE:  Remove all /100 division of Ownership Proportionate Perc from
//           measure expressions, now that the source column stores decimal
//           values (0.50) instead of whole-number percentages (50).
//
// SCOPE:    All measures in the model that reference Ownership Proportionate
//           Perc, @Ownership, or _OwnershipPct and divide by 100.
//
// PATTERNS HANDLED:
//   P1:  DIVIDE([Raw], 100) * Perc           → [Raw] * Perc
//   P2a: DIVIDE('Properties'[Perc], 100)      → 'Properties'[Perc]   (qualified)
//   P2b: DIVIDE([Perc], 100)                  → [Perc]               (unqualified)
//   P3:  DIVIDE([@pct], 100)                  → [@pct]
//   P4:  DIVIDE(LOOKUPVALUE(Perc, ...), 100)  → LOOKUPVALUE(Perc, ...)
//   P5:  [@Ownership] / 100                   → [@Ownership]
//   P6:  VALUE(Perc) / 100                    → VALUE(Perc)
//   P7:  COALESCE(Perc, 100) / 100            → COALESCE(Perc, 1)
//   P8:  ) / 100  (on line after MAX(Perc))   → )  (remove bare division)
//
// EXECUTE:  Run in Tabular Editor AFTER fix-ownership-pct-source.csx
//           Then save and refresh the model.
//
// ROLLBACK: Ctrl+Z in Tabular Editor (before save)
// =============================================================================

using System.Text.RegularExpressions;

var modified = new List<string>();
var skipped = new List<string>();
var manualReview = new List<string>();

// Track total regex hits per pattern for reporting
var patternHits = new Dictionary<string, int>
{
    { "P1_DIVIDE_Raw_100", 0 },
    { "P2a_DIVIDE_Perc_100_qualified", 0 },
    { "P2b_DIVIDE_Perc_100_unqualified", 0 },
    { "P3_DIVIDE_AtPct_100", 0 },
    { "P4_DIVIDE_Lookup_100", 0 },
    { "P5_AtOwnership_bare", 0 },
    { "P6_VALUE_Perc_bare", 0 },
    { "P7_COALESCE_100_bare", 0 },
    { "P8_bare_paren_div100", 0 }
};

// =============================================================================
// HELPER: Paren-balanced DIVIDE(X, 100) removal
// =============================================================================
// For cases where X contains nested parens (e.g., LOOKUPVALUE(...)),
// simple regex can't match. This helper finds DIVIDE( at a position,
// walks parens to find the matching ), verifies the second arg is 100,
// and returns just X.

Func<string, string, string> RemoveDivideBy100AroundMarker = (expr, marker) =>
{
    var result = expr;
    int searchFrom = 0;
    bool madeChange = true;
    
    // Iterate until no more changes (handles multiple occurrences)
    while (madeChange)
    {
        madeChange = false;
        
        // Find DIVIDE( positions
        var divMatch = Regex.Match(result.Substring(searchFrom), @"DIVIDE\s*\(", RegexOptions.IgnoreCase);
        if (!divMatch.Success)
            break;
        
        int divStart = searchFrom + divMatch.Index;
        int parenStart = divStart + divMatch.Length - 1; // position of '('
        
        // Walk parens to find matching ')'
        int depth = 1;
        int pos = parenStart + 1;
        int firstCommaAtDepth1 = -1;
        
        while (pos < result.Length && depth > 0)
        {
            char c = result[pos];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 1 && firstCommaAtDepth1 < 0)
                firstCommaAtDepth1 = pos;
            pos++;
        }
        
        if (depth != 0 || firstCommaAtDepth1 < 0)
        {
            searchFrom = divStart + 1;
            continue;
        }
        
        int closeParen = pos - 1;
        
        // Extract first arg (X) and second arg
        string firstArg = result.Substring(parenStart + 1, firstCommaAtDepth1 - parenStart - 1).Trim();
        string secondArgRaw = result.Substring(firstCommaAtDepth1 + 1, closeParen - firstCommaAtDepth1 - 1).Trim();
        
        // Second arg might be "100" or "100, 0" (with alternate result)
        // Strip any third arg: take just the part before the next comma at depth 0
        string secondArg = secondArgRaw;
        int commaInSecond = secondArgRaw.IndexOf(',');
        if (commaInSecond >= 0)
            secondArg = secondArgRaw.Substring(0, commaInSecond).Trim();
        
        // Check: does first arg contain our marker, and is second arg "100"?
        if (firstArg.Contains(marker) && secondArg == "100")
        {
            // Replace DIVIDE(X, 100) or DIVIDE(X, 100, alt) with just X
            result = result.Substring(0, divStart) + firstArg + result.Substring(closeParen + 1);
            madeChange = true;
            patternHits["P4_DIVIDE_Lookup_100"]++;
            // Don't advance searchFrom — re-scan from same position in case of nesting
        }
        else
        {
            searchFrom = divStart + 1;
        }
    }
    
    return result;
};

// =============================================================================
// PROCESS ALL MEASURES
// =============================================================================

foreach (var m in Model.AllMeasures)
{
    var expr = m.Expression;
    if (string.IsNullOrWhiteSpace(expr)) continue;
    
    // Only process measures that reference ownership-related columns
    bool refsOwnership = expr.Contains("Ownership Proportionate Perc")
                      || expr.Contains("@Ownership")
                      || expr.Contains("_OwnershipPct")
                      || expr.Contains("Ownership Perc");
    
    if (!refsOwnership) continue;
    
    var originalExpr = expr;
    
    // -------------------------------------------------------------------------
    // P1: DIVIDE([Raw] or [raw], 100) → [Raw] or [raw]
    //     Handles multi-line with varying whitespace
    // -------------------------------------------------------------------------
    {
        var pattern = @"DIVIDE\s*\(\s*\[(R|r)aw\]\s*,\s*100\s*\)";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P1_DIVIDE_Raw_100"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "[$1aw]");
    }
    
    // -------------------------------------------------------------------------
    // P2a: DIVIDE('Properties'[Ownership Proportionate Perc], 100)
    //      → 'Properties'[Ownership Proportionate Perc]
    //      (table-qualified references)
    // -------------------------------------------------------------------------
    {
        var pattern = @"DIVIDE\s*\(\s*'Properties'\[Ownership Proportionate Perc\]\s*,\s*100\s*\)";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P2a_DIVIDE_Perc_100_qualified"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "'Properties'[Ownership Proportionate Perc]");
    }
    
    // -------------------------------------------------------------------------
    // P2b: DIVIDE([Ownership Proportionate Perc], 100)
    //      → [Ownership Proportionate Perc]
    //      (unqualified — inside SUMMARIZECOLUMNS/ADDCOLUMNS scope)
    // -------------------------------------------------------------------------
    {
        var pattern = @"DIVIDE\s*\(\s*\[Ownership Proportionate Perc\]\s*,\s*100\s*\)";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P2b_DIVIDE_Perc_100_unqualified"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "[Ownership Proportionate Perc]");
    }
    
    // -------------------------------------------------------------------------
    // P3: DIVIDE([@pct], 100) → [@pct]
    // -------------------------------------------------------------------------
    {
        var pattern = @"DIVIDE\s*\(\s*\[@pct\]\s*,\s*100\s*\)";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P3_DIVIDE_AtPct_100"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "[@pct]");
    }
    
    // -------------------------------------------------------------------------
    // P4: DIVIDE(LOOKUPVALUE('Properties'[Ownership Proportionate Perc], ...), 100)
    //     → LOOKUPVALUE(...)
    //     Uses paren-balanced helper for nested parens
    // -------------------------------------------------------------------------
    if (expr.Contains("LOOKUPVALUE") && expr.Contains("Ownership Proportionate Perc"))
    {
        expr = RemoveDivideBy100AroundMarker(expr, "Ownership Proportionate Perc");
    }
    
    // -------------------------------------------------------------------------
    // P5: [@Ownership] / 100 → [@Ownership]
    // -------------------------------------------------------------------------
    {
        var pattern = @"\[@Ownership\]\s*/\s*100";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P5_AtOwnership_bare"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "[@Ownership]");
    }
    
    // -------------------------------------------------------------------------
    // P6: VALUE('Properties'[Ownership Proportionate Perc]) / 100
    //     → VALUE('Properties'[Ownership Proportionate Perc])
    //     Also handles variant with extra parens:
    //     ( VALUE(...) / 100 ) → ( VALUE(...) )
    // -------------------------------------------------------------------------
    {
        var pattern = @"(VALUE\s*\(\s*'Properties'\[Ownership Proportionate Perc\]\s*\))\s*/\s*100";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P6_VALUE_Perc_bare"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "$1");
    }
    
    // -------------------------------------------------------------------------
    // P7: COALESCE([Ownership Proportionate Perc], 100) / 100
    //     → COALESCE([Ownership Proportionate Perc], 1)
    //     The default "100" meant 100% as whole number. After decimal conversion
    //     the equivalent default is "1" (= 100%).
    // -------------------------------------------------------------------------
    {
        var pattern = @"COALESCE\s*\(\s*\[Ownership Proportionate Perc\]\s*,\s*100\s*\)\s*/\s*100";
        var matches = Regex.Matches(expr, pattern);
        patternHits["P7_COALESCE_100_bare"] += matches.Count;
        expr = Regex.Replace(expr, pattern, "COALESCE( [Ownership Proportionate Perc], 1 )");
    }
    
    // -------------------------------------------------------------------------
    // P8: Bare ) / 100 on lines near MAX(Ownership Proportionate Perc)
    //     Pattern in Homes Serviced Cost Internally:
    //       CALCULATE(MAX('Properties'[Ownership Proportionate Perc])) / 100
    //     We need to remove just the "/ 100" suffix.
    //     Strategy: match )\s*/\s*100 only when preceded by Ownership context
    // -------------------------------------------------------------------------
    if (expr.Contains("Ownership Proportionate Perc") || expr.Contains("Ownership Perc"))
    {
        // Look for: MAX('Properties'[Ownership Proportionate Perc])\s*)\s*/\s*100
        var pattern8a = @"(MAX\s*\(\s*'Properties'\[Ownership Proportionate Perc\]\s*\)\s*\))\s*/\s*100";
        var matches = Regex.Matches(expr, pattern8a);
        patternHits["P8_bare_paren_div100"] += matches.Count;
        expr = Regex.Replace(expr, pattern8a, "$1");
    }
    
    // -------------------------------------------------------------------------
    // CHECK: Did we actually change anything?
    // -------------------------------------------------------------------------
    if (expr != originalExpr)
    {
        m.Expression = expr;
        modified.Add(m.Table.Name + "::" + m.Name);
    }
    else
    {
        skipped.Add(m.Table.Name + "::" + m.Name);
    }
    
    // -------------------------------------------------------------------------
    // VERIFY: Flag measures that still have /100 near ownership references
    // -------------------------------------------------------------------------
    if (expr.Contains("/ 100") || expr.Contains("/100"))
    {
        // Check if the remaining /100 is near an ownership reference
        var lines = expr.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if ((line.Contains("/ 100") || line.Contains("/100"))
                && !line.TrimStart().StartsWith("//"))
            {
                // Check surrounding lines (±3) for ownership context
                bool nearOwnership = false;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                {
                    if (lines[j].Contains("Ownership") || lines[j].Contains("@Ownership") || lines[j].Contains("@pct"))
                    {
                        nearOwnership = true;
                        break;
                    }
                }
                
                if (nearOwnership)
                {
                    manualReview.Add(m.Table.Name + "::" + m.Name + " — residual /100 at line " + (i + 1) + ": " + line);
                }
            }
        }
    }
    
    // Also check for DIVIDE(..., 100) that wasn't caught
    if (Regex.IsMatch(expr, @"DIVIDE\s*\([^)]*,\s*100\s*\)") 
        && (expr.Contains("Ownership") || expr.Contains("@pct")))
    {
        // Verify it's ownership-related, not some other DIVIDE by 100
        manualReview.Add(m.Table.Name + "::" + m.Name + " — residual DIVIDE(..., 100) pattern detected");
    }
}

// =============================================================================
// REPORT
// =============================================================================

var report = "============================================================\n";
report += "fix-ownership-pct-measures.csx — Execution Report\n";
report += "============================================================\n\n";

report += "MEASURES MODIFIED (" + modified.Count + "):\n";
foreach (var m in modified)
{
    report += "  ✓ " + m + "\n";
}

report += "\nPATTERN HIT COUNTS:\n";
foreach (var kvp in patternHits)
{
    report += "  " + kvp.Key + ": " + kvp.Value + "\n";
}

if (skipped.Count > 0)
{
    report += "\nMEASURES WITH OWNERSHIP REF BUT NO /100 FOUND (" + skipped.Count + "):\n";
    foreach (var s in skipped)
    {
        report += "  - " + s + "\n";
    }
}

if (manualReview.Count > 0)
{
    report += "\n⚠ MANUAL REVIEW NEEDED (" + manualReview.Count + "):\n";
    foreach (var r in manualReview)
    {
        report += "  ⚠ " + r + "\n";
    }
}

report += "\n------------------------------------------------------------\n";
report += "NEXT STEPS:\n";
report += "  1. Review modified measures above\n";
report += "  2. Check any ⚠ manual review items\n";
report += "  3. Verify fix-ownership-pct-source.csx was also run\n";
report += "  4. Save the model\n";
report += "  5. Refresh the model immediately\n";
report += "------------------------------------------------------------\n";

Info(report);
