// ============================================================================
// Maintenance & Construction — Cleanup Unused Inactive Relationships
// ============================================================================
//
// PURPOSE: Remove 20 inactive relationships that are never referenced by
//          USERELATIONSHIP, CROSSFILTER, RELATED, or RELATEDTABLE in any
//          measure, calculated column, or calculation group item.
//
// VALIDATED: All 20 relationships confirmed inactive and unreferenced.
//            1 relationship excluded (Work Orders[Move In Work Order Prior
//            Project Scope ID] → Projects[Project Scope Id]) because it IS
//            used via USERELATIONSHIP in a calculated column.
//
// RUN IN: Tabular Editor (connected to model). Review changes before saving.
//         All changes are undoable with Ctrl+Z.
//
// ============================================================================

var deleted = new System.Collections.Generic.List<string>();
var notFound = new System.Collections.Generic.List<string>();

// ── Define all relationships to delete ──────────────────────────────────────

var toDelete = new[] {
    // Projects → Calendar (13)
    new { From = "Projects", FromCol = "Project Complete Dt Key",                      To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Out To Bid Cleaned Dt",                To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Released To Construction Cleaned Dt",  To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Rent Ready Cleaned Dt",                To = "Calendar", ToCol = "Date" },
    new { From = "Projects", FromCol = "Project Construction In Progress Cleaned Dt",  To = "Calendar", ToCol = "Date" },
    new { From = "Projects", FromCol = "Project Final Punch Cleaned Dt",               To = "Calendar", ToCol = "Date" },
    new { From = "Projects", FromCol = "Project Pending Scope Cleaned Dt",             To = "Calendar", ToCol = "Date" },
    new { From = "Projects", FromCol = "Project Actual Construction In Progress Dt Key", To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Actual Completed Dt Key",              To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Actual Final Punch Dt Key",            To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Actual Out To Bid Dt Key",             To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Actual Pre Construction Dt Key",       To = "Calendar", ToCol = "Date Key" },
    new { From = "Projects", FromCol = "Project Actual Released To Construction Dt Key", To = "Calendar", ToCol = "Date Key" },

    // Projects → ECD Date Table (1)
    new { From = "Projects", FromCol = "Estimated Completion Date", To = "ECD Date Table", ToCol = "Date" },

    // Property Violations → Calendar (3)
    new { From = "Property Violations", FromCol = "Prop Violation Dt",            To = "Calendar", ToCol = "Date" },
    new { From = "Property Violations", FromCol = "Prop Violation Cure Dt",       To = "Calendar", ToCol = "Date" },
    new { From = "Property Violations", FromCol = "Prop Violation Cure Deadline Dt", To = "Calendar", ToCol = "Date" },

    // Work Orders → Calendar (1)
    new { From = "Work Orders", FromCol = "Work Order Schedule Ts", To = "Calendar", ToCol = "Date" },

    // PO Detail → Properties (1)
    new { From = "PO Detail", FromCol = "PO Line Property Key", To = "Properties", ToCol = "Property Key" },

    // Work Order (Factless) → Resident (1)
    new { From = "Work Order (Factless)", FromCol = "Work Order Resident Key", To = "Resident", ToCol = "Resident Key" },
};

// ── Find and delete each relationship ───────────────────────────────────────

foreach (var def in toDelete)
{
    var rel = Model.Relationships.FirstOrDefault(r =>
        r.FromTable.Name == def.From &&
        r.FromColumn.Name == def.FromCol &&
        r.ToTable.Name == def.To &&
        r.ToColumn.Name == def.ToCol);

    if (rel == null)
    {
        notFound.Add(def.From + "[" + def.FromCol + "] → " + def.To + "[" + def.ToCol + "]");
    }
    else
    {
        // Safety check: confirm it's actually inactive
        if (rel.IsActive)
        {
            notFound.Add("SKIPPED (active!): " + def.From + "[" + def.FromCol + "] → " + def.To + "[" + def.ToCol + "]");
            continue;
        }

        rel.Delete();
        deleted.Add(def.From + "[" + def.FromCol + "] → " + def.To + "[" + def.ToCol + "]");
    }
}

// ── Report results ──────────────────────────────────────────────────────────

var report = "═══════════════════════════════════════════════════\n";
report +=    " UNUSED RELATIONSHIP CLEANUP RESULTS\n";
report +=    "═══════════════════════════════════════════════════\n\n";

report += "DELETED (" + deleted.Count + " of " + toDelete.Length + "):\n";
foreach (var d in deleted)
    report += "  ✓ " + d + "\n";

if (notFound.Count > 0)
{
    report += "\nNOT FOUND / SKIPPED (" + notFound.Count + "):\n";
    foreach (var n in notFound)
        report += "  ⚠ " + n + "\n";
}

report += "\n═══════════════════════════════════════════════════\n";
report += " Review changes in Tabular Editor before saving.\n";
report += " Ctrl+Z to undo if needed.\n";
report += "═══════════════════════════════════════════════════\n";

Info(report);
