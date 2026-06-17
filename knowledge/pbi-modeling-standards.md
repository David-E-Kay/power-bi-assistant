# Modeling Standards & Quality Gates

<!-- Codified team standards for Power BI semantic model quality. These rules
     are enforced during model development (via powerbi-semantic-model skill)
     and preserved here as team knowledge. Customize for your organization. -->

## Quality Rules

### Critical
- Schema design must follow star schema: fact/dimension separation, dedicated
  Date table, no snowflaking, minimal calculated tables
- Explicit measures enforced for all aggregatable numeric columns; the base
  column should be hidden
- Every measure must include a `formatString` definition
- Columns must include appropriate `summarizeBy` settings (e.g., Quantity → Sum,
  Stock Qty → Max); foreign keys must be hidden; no accidental aggregation
- Repeated DAX patterns should be centralized using DAX UDF functions

### Important
- Model object clarity: measures, columns, and tables should include a
  business-friendly description incorporating company terminology
  (see Company Verbiage section below)
- When using Power Query code, data source references (Server, Folder, etc.)
  should be configured as semantic model parameters
- Review naming conventions for consistency; if inconsistent or creating a new
  model, use the Naming Conventions below
- When using the `Web.Contents` Power Query connector, use `RelativePath` to
  avoid configuring multiple connections:
  ```powerquery
  Web.Contents(
      "https://baseurl",
      [RelativePath = "relative-path"]
  )
  
  ```
### DAX Commenting Standard
- Every measure with non-trivial logic must include inline DAX comments (`//`)
  explaining the *why*, not just the *what*
- Comment categories (use as needed per measure complexity):
  - **Relationship activation:** When USERELATIONSHIP or CROSSFILTER appears,
    comment which path is being activated and why the default path doesn't work
  - **Filter rationale:** When KEEPFILTERS, REMOVEFILTERS, or ALL appear, comment
    what filter behavior is being controlled and why
  - **Branch logic:** When IF/SWITCH branches exist (e.g., current vs. prior
    period, detail vs. total), comment what each branch handles
  - **Performance choice:** When a pattern was chosen for performance reasons
    (e.g., ADDCOLUMNS single-pass instead of AVERAGEX), note the trade-off
  - **Business logic:** When the DAX encodes a business rule, state the rule
- Use `// ---` separator comments to visually group logical sections in
  long measures (5+ lines of logic)
- Do NOT comment obvious DAX (e.g., `// sum the amount` above `SUM(...)`)
- When refactoring or optimizing an existing measure, update or add comments
  to reflect the current logic — stale comments are worse than no comments

### Nice to Have
- Model should include an `About` table describing author and version
  (see About Table section below)

## Naming Conventions

- **Tables:** Business-friendly names. Don't use terms like "Fact" or "Dim".
  Use plural names for fact tables, singular for dimensions
  (e.g., `Sales`, `Product`, `Customer`)
- **Columns:** Readable names with spaces (e.g., `Order Date`, `Product`,
  `Unit Price`). For dimension name columns, prefer the same name as the
  dimension (`Product` instead of `Product Name`)
- **Measures:** Clear naming patterns (`Total Sales`, `Total Quantity`,
  `# Customers`, `# Products`)
- **Measure variations** (time intelligence) follow consistent suffixes:
  - `[measure name]` — base measure
  - `[measure name (ly)]` — last year
  - `[measure name (ytd)]` — year to date
- Object names must not contain tabs, line breaks, or control characters
- Object names must not start or end with a space
- **Critical:** Always use exact case-sensitive names when referencing objects

## Company Verbiage

<!-- CUSTOMIZE THIS SECTION for your organization. Replace the placeholder
     descriptions below with language specific to your business domain.
     This verbiage is used when generating measure/column descriptions. -->

- [Your Organization] — one-line description of what the business does
- Key markets / geographies the business operates in
- Primary entity hierarchy (e.g., Region → Market → Site → Unit)
- Key operational domains (the subject areas your models cover)
- Core financial/reporting framework (e.g., the P&L or KPI structure used in reports)
- Analytical data warehouse / source platform (e.g., [your data warehouse])
- Power BI serves as the primary BI/analytics delivery layer

## About Table

When creating a new model, include an `About` table with columns: Key (Text),
Value (Text), Order (Number). Use Import mode with this M expression:

```powerquery
let
    Source = #table({"Key", "Value"}, {
        {"Developed by", "[Your Organization] - Data & Analytics"},
        {"Version", "1.0"},
        {"Description", "[Model Name]"},
        {"Last Refresh", DateTime.ToText(DateTime.LocalNow(), "yyyy-MM-dd HH:mm:ss")}
    }),
    #"Added Index" = Table.AddIndexColumn(Source, "Order", 1, 1),
    #"Changed Type" = Table.TransformColumnTypes(#"Added Index", {
        {"Key", type text}, {"Value", type text}, {"Order", Int64.Type}
    }),
    #"Reordered Columns" = Table.ReorderColumns(#"Changed Type", {"Key", "Value", "Order"})
in
    #"Reordered Columns"
```
