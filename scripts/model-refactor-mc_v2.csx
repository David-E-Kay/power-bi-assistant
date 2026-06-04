// ============================================================================
// model-refactor-v2.csx
// Target: Pre-refactor M&C .bim (before any C1/C2/Pass 1-4 changes)
//
// Changes applied:
//   N1  - Add direct Work Orders[Property Key] → Properties[Property Key]
//   A2  - Activate PO Detail[Custom Project Key] → Projects[Project Key]
//   E1  - Delete Work Order (Factless)[Work Order Property Key] → Properties[Property Key]
//   DAX-E1 - Remove CROSSFILTER refs to deleted E1 relationship (32 measures)
//   DAX-A2 - Remove USERELATIONSHIP refs to now-active A2 relationship (15 measures)
//   CLEANUP - Delete 17 unused inactive relationships
//
// NOT applied (reverted from original refactor):
//   C1  - Bridge→WO stays BIDIR (was changed to single in original)
//   C2  - OWO→WO stays BIDIR INACTIVE (was changed to single in original)
//   Pass 1-4 DAX fixes - Not needed since bridge stays bidir
//
// ============================================================================

// ============================================================================
// CONFIGURATION
// ============================================================================

var dryRun = true;  // Set false to apply changes

var applyN1 = true;   // Add WO→Properties direct FK
var applyA2 = true;   // Activate PO Detail→Projects
var applyE1 = true;   // Delete Bridge→Properties
var applyDaxE1 = true; // Remove CROSSFILTER on deleted Bridge→Properties
var applyDaxA2 = true; // Remove USERELATIONSHIP on activated PO Detail→Projects
var applyCleanup = true; // Delete unused inactive relationships

// ============================================================================
// COUNTERS
// ============================================================================

int n1Count = 0, a2Count = 0, e1Count = 0;
int daxE1Count = 0, daxA2Count = 0, cleanupCount = 0;
int daxE1Errors = 0, daxA2Errors = 0;
var warnings = new List<string>();
var details = new List<string>();

// ============================================================================
// HELPER: Smart comma/whitespace cleanup after removing a CALCULATE argument
// ============================================================================

string CleanCommas(string expr)
{
    // Remove double commas (with optional whitespace between)
    var result = System.Text.RegularExpressions.Regex.Replace(
        expr, @",\s*,", ",");

    // Remove trailing comma before closing paren: ", )" or ",\n)"
    result = System.Text.RegularExpressions.Regex.Replace(
        result, @",\s*\)", ")");

    // Remove leading comma after opening paren: "( ," or "(\n,"
    result = System.Text.RegularExpressions.Regex.Replace(
        result, @"\(\s*,", "(");

    // Remove leading comma after CALCULATE(: "CALCULATE (\n    ,"
    result = System.Text.RegularExpressions.Regex.Replace(
        result, @"(CALCULATE\s*\(\s*)\s*,", "$1", 
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    return result;
}

// ============================================================================
// N1: Add Work Orders[Property Key] → Properties[Property Key]
// ============================================================================

if (applyN1)
{
    // Check if relationship already exists
    var existingN1 = Model.Relationships.FirstOrDefault(r =>
        r.FromTable.Name == "Work Orders" && r.FromColumn.Name == "Property Key" &&
        r.ToTable.Name == "Properties" && r.ToColumn.Name == "Property Key");

    if (existingN1 != null)
    {
        warnings.Add("N1: Relationship Work Orders→Properties already exists. Skipping.");
    }
    else
    {
        // Verify columns exist
        var woTable = Model.Tables["Work Orders"];
        var propTable = Model.Tables["Properties"];

        if (woTable == null || propTable == null)
        {
            warnings.Add("N1: Work Orders or Properties table not found!");
        }
        else if (!woTable.Columns.Contains("Property Key"))
        {
            warnings.Add("N1: Work Orders[Property Key] column not found!");
        }
        else if (!propTable.Columns.Contains("Property Key"))
        {
            warnings.Add("N1: Properties[Property Key] column not found!");
        }
        else
        {
            if (!dryRun)
            {
                var rel = Model.AddRelationship();
                rel.FromColumn = woTable.Columns["Property Key"];
                rel.ToColumn = propTable.Columns["Property Key"];
                rel.FromCardinality = RelationshipEndCardinality.Many;
                rel.ToCardinality = RelationshipEndCardinality.One;
                rel.CrossFilteringBehavior = CrossFilteringBehavior.OneDirection;
                rel.IsActive = true;
            }
            n1Count = 1;
            details.Add("N1: Created Work Orders[Property Key] → Properties[Property Key] (active, single, M:1)");
        }
    }
}

// ============================================================================
// A2: Activate PO Detail[Custom Project Key] → Projects[Project Key]
// ============================================================================

if (applyA2)
{
    var a2Rel = Model.Relationships.FirstOrDefault(r =>
        r.FromTable.Name == "PO Detail" && r.FromColumn.Name == "Custom Project Key" &&
        r.ToTable.Name == "Projects" && r.ToColumn.Name == "Project Key");

    if (a2Rel == null)
    {
        warnings.Add("A2: PO Detail→Projects relationship not found!");
    }
    else if (a2Rel.IsActive)
    {
        warnings.Add("A2: PO Detail→Projects is already active. Skipping.");
    }
    else
    {
        if (!dryRun)
        {
            a2Rel.IsActive = true;
        }
        a2Count = 1;
        details.Add("A2: Activated PO Detail[Custom Project Key] → Projects[Project Key]");
    }
}

// ============================================================================
// E1: Delete Work Order (Factless)[Work Order Property Key] → Properties[Property Key]
// ============================================================================

if (applyE1)
{
    var e1Rel = Model.Relationships.FirstOrDefault(r =>
        r.FromTable.Name == "Work Order (Factless)" && r.FromColumn.Name == "Work Order Property Key" &&
        r.ToTable.Name == "Properties" && r.ToColumn.Name == "Property Key");

    if (e1Rel == null)
    {
        warnings.Add("E1: Bridge→Properties relationship not found! Already deleted?");
    }
    else
    {
        if (!dryRun)
        {
            e1Rel.Delete();
        }
        e1Count = 1;
        details.Add("E1: Deleted Work Order (Factless)[Work Order Property Key] → Properties[Property Key]");
    }
}

// ============================================================================
// DAX-E1: Remove CROSSFILTER on deleted Bridge→Properties relationship
//
// Matches (case-insensitive, flexible whitespace):
//   CROSSFILTER ( 'Properties'[Property Key], 'Work Order (Factless)'[Work Order Property Key], NONE )
//   CROSSFILTER ( 'Work Order (Factless)'[Work Order Property Key], 'Properties'[Property Key], NONE )
// ============================================================================

if (applyDaxE1)
{
    // Pattern matches CROSSFILTER with either column order
    var e1Pattern = new System.Text.RegularExpressions.Regex(
        @"CROSSFILTER\s*\(\s*" +
        @"(?:" +
            // Order 1: Properties → Bridge
            @"'?Properties'?\s*\[\s*(?:PROPERTY\s+KEY|Property\s+Key)\s*\]\s*,\s*" +
            @"'?Work\s+Order\s+\(Factless\)'?\s*\[\s*(?:WORK\s+ORDER\s+PROPERTY\s+KEY|Work\s+Order\s+Property\s+Key)\s*\]" +
        @"|" +
            // Order 2: Bridge → Properties
            @"'?Work\s+Order\s+\(Factless\)'?\s*\[\s*(?:WORK\s+ORDER\s+PROPERTY\s+KEY|Work\s+Order\s+Property\s+Key)\s*\]\s*,\s*" +
            @"'?Properties'?\s*\[\s*(?:PROPERTY\s+KEY|Property\s+Key)\s*\]" +
        @")" +
        @"\s*,\s*\w+\s*\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    int expectedE1 = 32;

    foreach (var m in Model.AllMeasures)
    {
        if (!e1Pattern.IsMatch(m.Expression)) continue;

        var original = m.Expression;
        var modified = e1Pattern.Replace(m.Expression, "");
        modified = CleanCommas(modified);

        if (modified != original)
        {
            daxE1Count++;
            details.Add("DAX-E1: " + m.Name);

            if (!dryRun)
            {
                m.Expression = modified;
            }
        }
    }

    if (daxE1Count != expectedE1)
    {
        warnings.Add("DAX-E1: Expected " + expectedE1 + " measures but matched " + daxE1Count + ". Review manually.");
    }
}

// ============================================================================
// DAX-A2: Remove USERELATIONSHIP on now-active PO Detail→Projects relationship
//
// Matches (case-insensitive, flexible whitespace):
//   USERELATIONSHIP ( 'Projects'[Project Key], 'PO Detail'[Custom Project Key] )
//   USERELATIONSHIP ( 'PO Detail'[Custom Project Key], Projects[Project Key] )
// ============================================================================

if (applyDaxA2)
{
    var a2Pattern = new System.Text.RegularExpressions.Regex(
        @"USERELATIONSHIP\s*\(\s*" +
        @"(?:" +
            // Order 1: Projects → PO Detail
            @"'?Projects'?\s*\[\s*(?:PROJECT\s+KEY|Project\s+Key)\s*\]\s*,\s*" +
            @"'?PO\s+Detail'?\s*\[\s*Custom\s+Project\s+Key\s*\]" +
        @"|" +
            // Order 2: PO Detail → Projects
            @"'?PO\s+Detail'?\s*\[\s*Custom\s+Project\s+Key\s*\]\s*,\s*" +
            @"'?Projects'?\s*\[\s*(?:PROJECT\s+KEY|Project\s+Key)\s*\]" +
        @")" +
        @"\s*\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    int expectedA2 = 15;

    foreach (var m in Model.AllMeasures)
    {
        if (!a2Pattern.IsMatch(m.Expression)) continue;

        var original = m.Expression;
        var modified = a2Pattern.Replace(m.Expression, "");
        modified = CleanCommas(modified);

        if (modified != original)
        {
            daxA2Count++;
            details.Add("DAX-A2: " + m.Name);

            if (!dryRun)
            {
                m.Expression = modified;
            }
        }
    }

    if (daxA2Count != expectedA2)
    {
        warnings.Add("DAX-A2: Expected " + expectedA2 + " measures but matched " + daxA2Count + ". Review manually.");
    }
}

// ============================================================================
// CLEANUP: Delete unused inactive relationships
// ============================================================================

if (applyCleanup)
{
    var unusedRels = new List<(string fromTable, string fromCol, string toTable, string toCol)>
    {
        ("Projects", "Project Complete Dt Key", "Calendar", "Date Key"),
        ("Work Orders", "Work Order Schedule Ts", "Calendar", "Date"),
        ("Projects", "Project Actual Completed Dt Key", "Calendar", "Date Key"),
        ("Projects", "Project Actual Final Punch Dt Key", "Calendar", "Date Key"),
        ("Projects", "Project Actual Out To Bid Dt Key", "Calendar", "Date Key"),
        ("Projects", "Project Actual Pre Construction Dt Key", "Calendar", "Date Key"),
        ("Projects", "Project Actual Released To Construction Dt Key", "Calendar", "Date Key"),
        ("Property Violations", "Prop Violation Dt", "Calendar", "Date"),
        ("Property Violations", "Prop Violation Cure Dt", "Calendar", "Date"),
        ("Property Violations", "Prop Violation Cure Deadline Dt", "Calendar", "Date"),
        ("PO Detail", "PO Line Property Key", "Properties", "Property Key"),
        ("PO Detail", "PO Line Vendor Key", "Vendors", "Vendor Key"),
        ("Projects", "Project Final Punch Cleaned Dt", "Calendar", "Date"),
        ("Projects", "Project Pending Scope Cleaned Dt", "Calendar", "Date"),
        ("Projects", "Project Rent Ready Cleaned Dt", "Calendar", "Date"),
        ("Projects", "Project Out To Bid Cleaned Dt", "Calendar", "Date Key"),
        ("Projects", "Project Released To Construction Cleaned Dt", "Calendar", "Date Key"),
    };

    int expectedCleanup = unusedRels.Count; // 17

    foreach (var (ft, fc, tt, tc) in unusedRels)
    {
        var rel = Model.Relationships.FirstOrDefault(r =>
            r.FromTable.Name == ft && r.FromColumn.Name == fc &&
            r.ToTable.Name == tt && r.ToColumn.Name == tc);

        if (rel == null)
        {
            warnings.Add("CLEANUP: Not found: " + ft + "[" + fc + "] → " + tt + "[" + tc + "]");
            continue;
        }

        if (rel.IsActive)
        {
            warnings.Add("CLEANUP: Relationship is ACTIVE (expected inactive): " + ft + "[" + fc + "]. Skipping.");
            continue;
        }

        if (!dryRun)
        {
            rel.Delete();
        }
        cleanupCount++;
    }

    if (cleanupCount != expectedCleanup)
    {
        warnings.Add("CLEANUP: Expected " + expectedCleanup + " but processed " + cleanupCount + ".");
    }
}

// ============================================================================
// REPORT
// ============================================================================

var sb = new System.Text.StringBuilder();
sb.AppendLine("══════════════════════════════════════════════════════");
sb.AppendLine(dryRun ? "  DRY RUN — No changes applied" : "  CHANGES APPLIED");
sb.AppendLine("══════════════════════════════════════════════════════");
sb.AppendLine();
sb.AppendLine("Summary:");
sb.AppendLine("  N1  (Add WO→Properties):        " + n1Count);
sb.AppendLine("  A2  (Activate PO Detail→Proj):   " + a2Count);
sb.AppendLine("  E1  (Delete Bridge→Properties):  " + e1Count);
sb.AppendLine("  DAX-E1 (Remove CROSSFILTER):     " + daxE1Count + " measures");
sb.AppendLine("  DAX-A2 (Remove USERELATIONSHIP): " + daxA2Count + " measures");
sb.AppendLine("  CLEANUP (Unused inactive rels):   " + cleanupCount);
sb.AppendLine();

if (warnings.Count > 0)
{
    sb.AppendLine("⚠️  Warnings (" + warnings.Count + "):");
    foreach (var w in warnings)
        sb.AppendLine("  " + w);
    sb.AppendLine();
}

sb.AppendLine("Details:");
foreach (var d in details)
    sb.AppendLine("  " + d);

Info(sb.ToString());
