// Bidir Impact Analysis: Property ↔ Occupancy
// Run in TE3 connected to the Occupancy semantic model.
// Identifies measures that reference both tables — candidates for
// CROSSFILTER after changing the relationship to single-direction.

var table1 = "Property";
var table2 = "Occupancy";

// --- 1. Find and report the relationship(s) between the two tables ---

var rels = Model.Relationships
    .OfType<SingleColumnRelationship>()
    .Where(r =>
        (r.FromTable.Name == table1 && r.ToTable.Name == table2) ||
        (r.FromTable.Name == table2 && r.ToTable.Name == table1))
    .ToList();

var sb = new System.Text.StringBuilder();

sb.AppendLine("═══════════════════════════════════════════════════════════════");
sb.AppendLine("  BIDIR IMPACT ANALYSIS: " + table1 + " ↔ " + table2);
sb.AppendLine("═══════════════════════════════════════════════════════════════");
sb.AppendLine();

if (rels.Count == 0)
{
    sb.AppendLine("  ⚠ No relationship found between " + table1 + " and " + table2);
    sb.AppendLine("    Verify table names and run again.");
    var errPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        + @"\occupancy-bidir-impact.txt";
    SaveFile(errPath, sb.ToString());
    Info("Report saved to: " + errPath);
    return;
}

foreach (var rel in rels)
{
    var dir = rel.CrossFilteringBehavior.ToString();
    sb.AppendLine("  Relationship:  " + rel.FromTable.Name + "[" + rel.FromColumn.Name + "]"
        + " → " + rel.ToTable.Name + "[" + rel.ToColumn.Name + "]");
    sb.AppendLine("  Cardinality:   " + rel.FromCardinality + " → " + rel.ToCardinality);
    sb.AppendLine("  Filter dir:    " + dir);
    sb.AppendLine("  Active:        " + rel.IsActive);
    sb.AppendLine();
}

sb.AppendLine("  Planned change: OneDirection (" + table1 + " filters " + table2 + ")");
sb.AppendLine();

// --- 2. Scan all measures for references to both tables ---
// Uses string matching on the raw DAX expression — covers 'Table'[col],
// Table[col], and standalone 'Table' references reliably.

var needsReview = new List<Measure>();
var alreadyHandled = new List<Tuple<Measure, string>>();
int totalScanned = 0;

foreach (var m in Model.AllMeasures)
{
    totalScanned++;
    if (string.IsNullOrWhiteSpace(m.Expression)) continue;

    var expr = m.Expression;

    // Match 'TableName' (quoted standalone or before [col]) and TableName[ (unquoted column ref)
    bool refTable1 =
        expr.IndexOf("'" + table1 + "'", StringComparison.OrdinalIgnoreCase) >= 0 ||
        expr.IndexOf(table1 + "[",       StringComparison.OrdinalIgnoreCase) >= 0;

    bool refTable2 =
        expr.IndexOf("'" + table2 + "'", StringComparison.OrdinalIgnoreCase) >= 0 ||
        expr.IndexOf(table2 + "[",       StringComparison.OrdinalIgnoreCase) >= 0;

    if (!refTable1 || !refTable2) continue;

    bool hasCrossfilter = expr.IndexOf("CROSSFILTER",    StringComparison.OrdinalIgnoreCase) >= 0;
    bool hasUserel      = expr.IndexOf("USERELATIONSHIP", StringComparison.OrdinalIgnoreCase) >= 0;

    if (hasCrossfilter || hasUserel)
    {
        var tag = hasCrossfilter && hasUserel ? "CROSSFILTER + USERELATIONSHIP"
                : hasCrossfilter ? "CROSSFILTER"
                : "USERELATIONSHIP";
        alreadyHandled.Add(Tuple.Create(m, tag));
    }
    else
    {
        needsReview.Add(m);
    }
}

// --- 3. Report ---

sb.AppendLine("───────────────────────────────────────────────────────────────");
sb.AppendLine("  MEASURES REFERENCING BOTH TABLES — NEEDS REVIEW (" + needsReview.Count + ")");
sb.AppendLine("───────────────────────────────────────────────────────────────");

if (needsReview.Count == 0)
{
    sb.AppendLine("  (none)");
}
else
{
    sb.AppendLine(String.Format("  {0,-40} {1,-30} {2}",
        "Measure", "Table", "Folder"));
    sb.AppendLine("  " + new string('─', 90));

    foreach (var m in needsReview.OrderBy(m => m.Table.Name).ThenBy(m => m.Name))
    {
        var folder = string.IsNullOrEmpty(m.DisplayFolder) ? "(root)" : m.DisplayFolder;
        sb.AppendLine(String.Format("  {0,-40} {1,-30} {2}",
            m.Name.Length > 38 ? m.Name.Substring(0, 38) + "…" : m.Name,
            "[" + m.Table.Name + "]",
            folder));
    }
}

sb.AppendLine();
sb.AppendLine("───────────────────────────────────────────────────────────────");
sb.AppendLine("  ALREADY HAS CROSSFILTER / USERELATIONSHIP (" + alreadyHandled.Count + ")");
sb.AppendLine("───────────────────────────────────────────────────────────────");

if (alreadyHandled.Count == 0)
{
    sb.AppendLine("  (none)");
}
else
{
    sb.AppendLine(String.Format("  {0,-40} {1,-30} {2}",
        "Measure", "Table", "Contains"));
    sb.AppendLine("  " + new string('─', 90));

    foreach (var item in alreadyHandled.OrderBy(x => x.Item1.Table.Name).ThenBy(x => x.Item1.Name))
    {
        var m = item.Item1;
        sb.AppendLine(String.Format("  {0,-40} {1,-30} {2}",
            m.Name.Length > 38 ? m.Name.Substring(0, 38) + "…" : m.Name,
            "[" + m.Table.Name + "]",
            item.Item2));
    }
}

sb.AppendLine();
sb.AppendLine("───────────────────────────────────────────────────────────────");
sb.AppendLine("  SUMMARY");
sb.AppendLine("───────────────────────────────────────────────────────────────");
sb.AppendLine("  Total measures scanned:          " + totalScanned);
sb.AppendLine("  Referencing both tables:          " + (needsReview.Count + alreadyHandled.Count));
sb.AppendLine("    Needs review:                   " + needsReview.Count);
sb.AppendLine("    Already handled:                " + alreadyHandled.Count);

var outPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
    + @"\occupancy-bidir-impact.txt";
SaveFile(outPath, sb.ToString());
Info("Report saved to: " + outPath);
