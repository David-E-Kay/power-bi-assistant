"""Generate testLines + groupByColumns for the M&C baseline regression run.

Cross-joins a deduplicated measure list with 10 contexts (1 grand_total +
7 single-dim + 2 cross-product) and prints the two blocks ready to paste
into scripts/capture-snapshot.csx.

Rerun whenever the measure set or context matrix changes. The MEASURES list
mirrors output/mc-baseline-measures.txt — the canonical source kept under
output/ so the filtered scope can be reviewed in isolation. Keep both in sync.
"""
from __future__ import annotations

# 195 measures = Bucket D (core operational, ~184) + Budget base (~11).
# Excludes time intelligence (MoM/YoY/QoQ/MTD/YTD/QTD/Prior/SPLY), Open Work
# Order / EOP variants, and simple-filter wrappers. Sourced from
# artifacts/model-schema/model-schema-mc.md by an Explore agent and reviewed.
MEASURES: list[str] = [
    "% of Homes with Open Violations",
    "% of Properties with In House Move In Work Orders",
    "% of Properties with Move In Work Orders",
    "% of Properties with Vendor Move In Work Orders",
    "% of Total Work Orders",
    "1st & 2nd Warning Violations",
    "Active CIP-RR Exceptions Project Avg Days Aging",
    "Active CIP-RR Exceptions Project Avg Days CIP",
    "Active CIP-RR Pass Rate",
    "Active MO-RR Pass Rate",
    "Active Pre-Construction Exceptions Project Avg Days Aging",
    "Active Pre-Construction Pass Rate",
    "Annualized Avg Project Cost per Property by Project Complete Date",
    "Annualized Total Cost Per Property",
    "Appian fraction count",
    "Appian total  count",
    "Appian total available",
    "Approved Cost Variance",
    "Approved Cost Variance %",
    "Available Work order as % of total",
    "Average Casualty Work Order Costs Complete Date",
    "Average Cost per Homes Serviced (Budget)",
    "Average Cost per Property by Work Order Complete Date",
    "Average Cost per Vendor",
    "Average Deferred Rehab Project Cost by Completed Date (Budget)",
    "Average HOA Work Order Costs Complete Date",
    "Average Home Count",
    "Average In-House Maintenance Work Order Costs Complete Date",
    "Average Line Item Amt",
    "Average Number of Work Orders per Property by Work Order Created Date",
    "Average Number of Work Orders per Vendor by Work Order Created Date",
    "Average Project Cost by Completed Date",
    "Average Project Cost by Created Date",
    "Average Project Cost by Estimated Completion Date",
    "Average Project Cost(Occupancy)",
    "Average Resident Tenure by Project Complete Date",
    "Average Response time",
    "Average Tenure of Turn",
    "Average Turn Project Cost by Completed Date (Budget)",
    "Average Work Order Cost by Work Order Completed Date",
    "Average Work Order Cost by Work Order Created Date",
    "Average Work Order Days To Complete by Completed Date",
    "Average Work Order Days To Complete by Created Date",
    "Average Year Built by Project completed date",
    "Avg Completed Occupied Maintenance Project Cost",
    "Avg Daily Open Properties %",
    "Avg Daily Open Properties Count",
    "Avg Daily Open Vendors Count",
    "Avg Move In Work Order Cost",
    "Avg Property Underwritten Budget $",
    "Avg Work Order Age",
    "Canceled Violations",
    "Closed  Violations",
    "Column Space",
    "Completed CIP-RR Pass Rate",
    "Completed MO-RR Pass Rate",
    "Completed Occupied Maintenance Project Costs",
    "Completed Pre-Construction Pass Rate",
    "Count of Move Ins By Resident Move In Date",
    "Count of Move Out Exception Properties",
    "Count of Properties Do Not Publish List",
    "Count of Properties with > 1 Move In Work Orders by Resident Move In Date",
    "Count of Properties with Move In Work Orders by Resident Move In Date",
    "Count of Properties with No Self-Tour Smart Home",
    "Count of Properties with No Virtual Tour/Inside Map",
    "Count of Properties with Projects by Completed Date",
    "Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX",
    "Count of Properties with Projects by Created Date",
    "Count of Properties with Smart Homes by Gateway Assignment Date",
    "Count of Properties(Acquisitions)",
    "Count of Rent Ready Exception Projects",
    "Count of Rent Ready Exception Properties",
    "Count of Vacant Properties Missing Scope",
    "Count of Work Orders",
    "Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX",
    "CountOfpCard",
    "Courtesy Violations",
    "Cumulative % Properties with Smart Homes by Gateway Assignment Date",
    "Cumulative Count of Properties with Smart Homes by Gateway Assignment Date",
    "Current Backlog Ratio",
    "Cut Off Review Threshold Costs",
    "Deferred Rehab Labor Cost",
    "Deferred Rehab Project Count by Completed Date (Budget)",
    "Delinquent Violations",
    "Dispo Prep Labor Cost",
    "FieldServices Data as of TS",
    "Final Warning Violations",
    "First Time Fix Rate %",
    "Homes Serviced (Budget)",
    "Homes Serviced By Vendor & Technician",
    "Homes Serviced Internally (Budget)",
    "Homes Serviced Internally Work Order Costs by Completed Date",
    "Homes Serviced Internally base",
    "Homes Serviced by Vendor (Budget)",
    "Homes Serviced by Vendor base",
    "Homes with Open  Violations",
    "IH Labor Cost",
    "IHM Completed Work Order",
    "IHM Completed Work Order Count (by Created Date)",
    "IHM Completion %",
    "IHM Completion % (by Completed Date)",
    "IHM WO's Completed",
    "In House Work order Created",
    "In House Work order completed",
    "In-House Maintenance Work Order Costs Complete Date",
    "In-House Utilization Rate (excl Appliances) Project Cost by Completed Date",
    "Initial Rehab Labor Cost",
    "Legal Violations",
    "Maintenance Labor Cost",
    "Max",
    "Median Work Order Costs by Created Date",
    "Min",
    "Move In Work Order Costs by Resident Move In Date",
    "Move In Work Order Count by Resident Move In Date",
    "Number of technicians",
    "Occupied Count",
    "Open  Violations",
    "P-Cards Cost",
    "PO Costs",
    "PO Line Item Count",
    "PO Work Order Count",
    "Percentage Total",
    "Physical Occupancy",
    "Project Approved Cost",
    "Project Average Days to Complete",
    "Project Avg CIP-RR Timeline",
    "Project Avg Days Aging",
    "Project Avg Days CIP",
    "Project Avg Days Past ECD",
    "Project Avg MO-RR Timeline",
    "Project Avg Pre-Construction Timeline",
    "Project Cost by Completed Date",
    "Project Cost by Created Date",
    "Project Cost by Estimated Completion Date",
    "Project Count by Created Date (Ignore 0 and Nulls)",
    "Project Count by Estimated Completion Date",
    "Project Count by Estimated Completion Date (Ignore 0 and Nulls)",
    "Project Count from Properties with Multiple Scopes (Exceptions)",
    "Projected Avg Project Cost (Actual + ECD)",
    "Projected Project Cost (Actual + ECD)",
    "Projected Project Count (Actual + ECD)",
    "Projects Completed by Occupied Homes",
    "Properties Serviced Internally %",
    "Properties Serviced Internally % (Budget)",
    "Property Count by Acquisition Date (Cumulative)",
    "Property Count with Multiple Scopes (Exceptions)",
    "Property Rental Home Count Average",
    "Property Underwritten Budget $",
    "Property Unit Count",
    "Rate of Properties Do Not Publish List",
    "Rate of Properties with No Self-Tour Smart Home",
    "Rate of Properties with No Virtual Tour/Inside Map",
    "Rate of Replacement",
    "Rental Home Count",
    "Repair Type Cost Cumulative %",
    "Repair Type Line Item Count Cumulative %",
    "Request Market Vendor",
    "Request Market Vendor %",
    "Return trip needed",
    "Return trip needed %",
    "Scope Line Item Usage",
    "Smart Home Installation Amount",
    "Smart Home Installed Properties % by Gateway Assignment Date",
    "Square Footage",
    "Superintendent % of Move Ins with Work Orders by Resident Move In Date",
    "T-3 P-Cards Cost",
    "Tech count",
    "Technician Assigned Work Orders",
    "Total Available Work orders",
    "Total HOA Homes",
    "Total Project Cost",
    "Total Project Count",
    "Total Property Count with Open In Progress Dates",
    "Total Violation Amt",
    "Total Violations",
    "Total Wo's Completed",
    "Total Work Order Violation Amt",
    "Total property count",
    "TotalRank",
    "TotalRank FT",
    "TotalRank HPD",
    "TotalRankWO",
    "Turn Project Count by Completed Date (Budget)",
    "Unique Scope ID",
    "Vendor Count",
    "Vendor Move In Work Order Count by Resident Move In Date",
    "Work Order Costs by Completed Date 80th Percentile",
    "Work Order Costs by Completed Date 80th Percentile Trailing 12 Month",
    "Work Order Count by Completed Date",
    "Work Order Count by Created Date",
    "Work Order Days to Complete",
    "Work Order Property Count",
    "Work Order Vendor Count",
    "Work Orders Completed Trailing 30 Days",
    "count of tech",
]

# Context label -> DAX column expression.
# "grand_total" is a reserved label handled specially by capture-snapshot.csx
# (SUMMARIZECOLUMNS with no grouping); it must NOT appear in groupByColumns.
# Cross-product entries use "|" as separator; first column = TOPN partition.
CONTEXTS: list[tuple[str, str | None]] = [
    ("grand_total",          None),
    ("by_month",             "'Calendar'[Start of Month]"),
    ("by_prop_toggle",       "'Proportionate Ownership Toggle'[Proportionate Values]"),
    ("by_market",            "'Properties'[Property Market Reporting]"),
    ("by_scope",             "'Projects'[Project Scope Type Desc]"),
    ("by_wo_status",         "'Work Orders'[Work Order Status Desc]"),
    ("by_vendor",            "'Vendors'[Vendor Name]"),
    ("by_repair_type",       "'Repair Type'[Repair Type]"),
    ("by_market_x_month",    "'Properties'[Property Market Reporting]|'Calendar'[Start of Month]"),
    ("by_wo_status_x_month", "'Work Orders'[Work Order Status Desc]|'Calendar'[Start of Month]"),
]


def main() -> None:
    seen = set()
    measures = []
    for m in MEASURES:
        if m not in seen:
            seen.add(m)
            measures.append(m)

    total = len(measures) * len(CONTEXTS)
    print(f"// {len(measures)} unique measures x {len(CONTEXTS)} contexts = {total} test cases")
    print()

    print("// ── testLines block (paste between the braces at lines 304-315) ──")
    print('var testLines = new List<string>')
    print("{")
    tid = 1
    for m in measures:
        m_escaped = m.replace('"', '\\"')
        for ctx_label, _ in CONTEXTS:
            print(f'    "t{tid:04d}|{m_escaped}|{ctx_label}",')
            tid += 1
    print("};")
    print()

    print("// ── groupByColumns block (paste between the braces at lines 320-331) ──")
    print('var groupByColumns = new Dictionary<string, string>')
    print("{")
    for ctx_label, col in CONTEXTS:
        if col is None:
            continue  # grand_total is reserved, not in dict
        print(f'    {{ "{ctx_label}", "{col}" }},')
    print("};")


if __name__ == "__main__":
    main()
