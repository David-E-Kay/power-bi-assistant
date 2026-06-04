---
confluence_id: "2475098115"
space_key: "DATA"
page_title: "[SOP] DAX and Data Modeling"
page_url: "https://triconah.atlassian.net/wiki/spaces/DATA/pages/2475098115/SOP+DAX+and+Data+Modeling"
last_modified: "Apr 10, 2026"
last_synced: "2026-05-09T00:00:00Z"
author: "David Kay"
labels: []
---

# [SOP] DAX and Data Modeling

To ensure best user experience, model performance, and reduce rework, the following general themes should be adhered to for every request. Regardless of Best Practice Rules being analyzed programmatically via tooling or as part of the PR process, these principles should guide how we develop, so that we aren't catching issues at the time of PR requests or after the fact.

See for additional examples of antipatterns.

## 1. Schema Design

### 1.1 Star Schema

All semantic models must follow a star schema topology: clearly separated fact and dimension tables, a dedicated Date table, no snowflaking unless justified by a documented design decision, and minimal use of calculated tables.

- Fact tables store transactional or snapshot grain data and connect to dimensions via foreign keys.
- Dimension tables store descriptive attributes. Each dimension is a single, denormalized table (no chained lookups).
- Bridge (factless fact) tables are permitted only for true many-to-many relationships where denormalization would cause data quality issues. Document the rationale in a design decisions log.
- Calculated tables limited to only scenarios where a measure is needed in the table creation and replicating said measure in SQL in EDW would be impractical.

### 1.2 Date Table

Every model must include a dedicated Date table marked as a date table. This table drives all time intelligence. Additional date columns on fact tables (e.g., completion date, ship date) connect to the Date table via inactive relationships and are activated with USERELATIONSHIP in measures.

Ensure that Auto Date Table is disabled for all models in Power BI desktop.

### 1.3 Relationship Directionality

- Default to single-direction cross-filtering. Bidirectional relationships cause performance overhead (full table scans) and ambiguity risk.
- Use bidirectional only when required for filter propagation through bridge tables, and prefer activating it at runtime via CROSSFILTER in measures rather than setting it as a default.
- Many-to-many relationships must use single-direction cross-filtering.

### 1.4 Relationship Integrity

- Never remove an inactive relationship without first auditing all DAX measures for USERELATIONSHIP or CROSSFILTER references to that relationship.
- Relationship columns on both sides must share the same data type.

### 1.5 Measure Table

- Always have a dedicated Measure table.
- Ensure that no measures are added to other tables.
  - Do NOT move measures from one table to another without explicit approval from D&A leadership.
    - In live connected reports, measure table name reference is included in the visual's metadata.
    - Changing the measure table will break visuals in live connected reports.
    - Fabric notebook using Semantic Link Labs can automate the process of fixing the visuals, but must be coordinated with D&A leadership and business users.
- Organize measures in folders to create a clean, logical structure.

## 2. Naming Conventions

### 2.1 Tables

| Convention | Example | Anti-Pattern |
| --- | --- | --- |
| Dimension tables: singular nouns, business-friendly | Customer, Product, Date | DIM_Customer, dim_date |
| Fact tables: plural nouns | Orders, Invoices, Work Orders | FACT_Orders, fct_Invoice |
| No technical prefixes (DIM_, FACT_, STG_) | Sales | FACT_Sales |

### 2.2 Columns

- Use natural language with spaces and title case: `Order Date`, not `OrderDate` or `order_date`.
- Key columns: suffix with `Key` and hide from report view.
- No abbreviations unless universally understood (e.g., ID, URL). Spell out: `Ship Date`, not `shp_dt`.

### 2.3 Measures

| Pattern | Convention | Examples |
| --- | --- | --- |
| Base measure | [Aggregation] [Entity] | Total Sales, Total Quantity |
| Percentage measures | `%` prefix or `(%)` suffix | % Margin, Occupancy (%) |
| Time intelligence | Standard suffix | Total Sales (ytd), Revenue (ly) |
| Supporting/helper measures | `_` prefix, hidden | _Base Revenue |
| Measure references in DAX | Unqualified (no table prefix) | `[Total Sales]`, not `Table[Total Sales]` |
| Column references in DAX | Fully qualified | `'Sales'[Amount]`, not `[Amount]` |

### 2.4 Display Folders

- Use numbered prefixes for consistent ordering: `1. Product Hierarchy`, `2. Attributes`, `5. Keys`.
- Use backslash for subfolder nesting: `2. MTD\Actuals`.

## 3. Column & Table Properties

### 3.1 Explicit Measures

Every aggregatable numeric column must have a corresponding explicit measure (SUM, AVERAGE, COUNT, etc.). The base column must then be hidden from the report field list. This prevents implicit aggregation surprises.

### 3.2 SummarizeBy

Set `SummarizeBy` to `None` on all visible numeric columns to prevent accidental implicit aggregation. Foreign key columns must be hidden entirely.

### 3.3 Format Strings

Every measure must have a `formatString` defined. Common patterns:

| Type | Format String |
| --- | --- |
| Currency (no decimals) | `"$"#,0` |
| Currency (2 decimals) | `"$"#,0.00` |
| Percentage | `0.0%` |
| Integer with comma separator | `#,0` |
| Decimal (2 places) | `#,0.00` |

### 3.4 Data Types

- Never use Double (floating point) for financial data. Use Decimal to avoid precision errors.
  - The Decimal data type in Power BI is limited to 4 digits after the decimal point. If your source data requires more precision (e.g., exchange rates, scientific measurements), you'll get silent truncation.
- When a column stores percentages as whole numbers (e.g., 85 meaning 85%), handle the `/100` conversion in the source or ETL layer, not in DAX or Power Query.
- Be aware of TEXT-type columns used in arithmetic. Wrap with `VALUE()` explicitly; implicit casts silently return BLANK on non-numeric values.
  - Ideally, convert the value to numeric at the source in the query to avoid needing to use `VALUE()`.
- Relationship columns should be Integer.
  - Text-based join keys (GUIDs, composite strings) produce larger dictionaries, slower joins, and worse compression than Int64 surrogate keys.

### 3.5 Descriptions & Documentation

- All visible measures, columns, and tables should have a business-friendly description.
- Use company-specific terminology in descriptions to align with how stakeholders talk about the data.

### 3.6 IsAvailableInMDX

Set `IsAvailableInMDX` to `false` on hidden columns not used in hierarchies, sort-by, or variations. This reduces processing overhead.

## 4. DAX Authoring Standards

### 4.1 Division

Always use `DIVIDE()` instead of the `/` operator. DIVIDE handles division by zero gracefully by returning BLANK (or a specified alternate value), avoiding runtime errors.

```dax
// Correct
DIVIDE( [Revenue], [Units Sold] )

// Avoid
[Revenue] / [Units Sold]
```

### 4.2 Error Handling

Do not use `IFERROR`. It masks real errors and degrades performance because the engine must evaluate both the expression and the fallback. Fix the root cause instead, or use DIVIDE for division-related errors.

### 4.3 Filter Arguments in CALCULATE

Do not use `FILTER` on an entire table as a CALCULATE filter argument when filtering by a single column. Use a direct Boolean predicate or KEEPFILTERS instead.

```dax
// Correct
CALCULATE( [Total Sales], 'Product'[Category] = "Electronics" )

// Correct (preserving external filters)
CALCULATE( [Total Sales], KEEPFILTERS( 'Product'[Category] = "Electronics" ) )

// Avoid
CALCULATE( [Total Sales], FILTER( 'Product', 'Product'[Category] = "Electronics" ) )
```

When filtering by a measure value, use FILTER on VALUES of the column (not the full table) or FILTER on ALL of the column, depending on whether external filters should be preserved.

### 4.4 USERELATIONSHIP & CROSSFILTER

- USERELATIONSHIP activates an inactive relationship but inherits its stored direction. It only works inside CALCULATE or CALCULATETABLE.
- CROSSFILTER changes cross-filter direction at runtime. It cannot activate an inactive relationship on its own.
- When an inactive relationship needs bidirectional propagation, use both together: USERELATIONSHIP to activate, CROSSFILTER(..., BOTH) to set direction.

### 4.5 Shared Filter Hoisting

When a measure combines multiple CALCULATE blocks that share the same filter arguments, hoist the shared filters into a single outer CALCULATE wrapper. Inner CALCULATE calls should contain only their distinguishing filters. This reduces engine overhead and improves readability.

### 4.6 Redundant Filter Elimination

When a measure wraps another measure in CALCULATE, trace the dependency chain. If the inner measure already applies the same filter, the outer filter is a no-op and should be removed. However, subset filters that narrow the context (e.g., filtering to a specific status within a broader category) are additive and must be retained.

### 4.7 Variables (VAR / RETURN)

Use VAR for readability and to prevent repeated evaluation of the same subexpression. The engine evaluates each VAR at most once, so extracting a shared calculation into a VAR can improve both clarity and performance.

### 4.8 TREATAS over INTERSECT

For virtual relationships, use TREATAS instead of INTERSECT. TREATAS is more efficient and provides better performance for propagating filters across tables without a physical relationship. However, first evaluate WHY a physical relationship cannot or does not exist, and only use virtual relationships as a last resort.

## 5. Calculated Columns

Ideally all derived columns should be done in EDW; the below reference is just for the exceptions. The below information is still relevant as to WHY the column is useful — it's just that the calculation aspect should be pushed to EDW as much as possible.

Make sure to document the decision for why this was necessary in the user story dev notes.

### 5.1 When to Use

Calculated columns are appropriate only for values used purely in calculations (e.g., a pre-computed ownership percentage for weighted averages, or a pre-computed bridge lookup to avoid expensive runtime CROSSFILTER). They are computed at refresh time and stored in memory.

### 5.2 When Not to Use

- Never denormalize dimension fields used as report slicers onto fact tables. Those fields must stay on the dimension table to filter naturally through relationships.
- If the same calculation could be a measure, prefer the measure. Calculated columns increase model size permanently.
- Tables with more than 10 calculated columns should be reviewed for over-reliance on this pattern. Consider pushing logic to the ETL/source layer.

### 5.3 Bridge Traversal Pattern

When a measure traverses a bridge table and the runtime CROSSFILTER cost is excessive, pre-compute the bridge lookup as a calculated column on the fact or snapshot table in the SQL layer, or worse case, in DAX using LOOKUPVALUE or RELATED. This trades model size for query speed and can yield 10-60x improvements.

## 6. Performance

### 6.1 Trace-Driven Optimization

Never optimize speculatively. All performance work must start from a DAX Studio trace with Server Timings enabled.

- Ensure the cache is cleared before capturing any trace statistics.
- Before running the trace, ensure the following are all turned on:
  - All Queries
  - Query Plan
  - Server Timings
- Export the results and use an LLM to parse for issues, bottlenecks, or other items.

General patterns to watch for:

| Indicator | What It Means | Action |
| --- | --- | --- |
| FE > 60% of total time | Formula engine iterating row-by-row | Simplify DAX, reduce iterators, push to SE |
| Large SE row counts (100K+) | Bridge scan, cross-join, or bidir expansion | Add direct FK, pre-compute calc column, or remove bidir |
| Repeated scans of same table | Query plan re-evaluating same context | Extract to VAR, consolidate CALCULATE |
| Same bridge scan across many measures | Structural bottleneck, not DAX | Consider relationship refactor |

### 6.2 Iterator Functions on Large Tables

SUMX, COUNTX, AVERAGEX, and other iterators scale poorly on tables beyond 500K rows. For bridge tables or large fact tables, replace with pre-aggregated calculated columns or push aggregation to the ETL/source layer.

Iterators evaluate an expression row-by-row over a table, then aggregate the results. They're powerful but expensive — the engine must materialize the table in the Formula Engine (FE) and loop through every row. This makes them the most common source of slow measures.

**When to use iterators:**

- When the per-row calculation can't be expressed as a simple column aggregation — for example, `SUMX( Sales, Sales[Qty] * Sales[UnitPrice] )` where no pre-computed Amount column exists.
- When you need conditional aggregation that varies per row — for example, `SUMX( Sales, IF( Sales[IsReturn], -Sales[Amount], Sales[Amount] ) )`.
- When computing weighted averages where the weight differs per row.

**When NOT to use iterators:**

- When a simple aggregator works — `SUM( Sales[Amount] )` is always faster than `SUMX( Sales, Sales[Amount] )`. The simple aggregator runs entirely in the Storage Engine; the iterator forces a Formula Engine loop.
- When iterating over a bridge or large junction table (500K+ rows). Pre-compute the value as a calculated column at refresh time instead.
- When nesting CALCULATE inside an iterator over a large table — each row triggers a separate CALCULATE evaluation, which can multiply query time by orders of magnitude.

Avoid `FILTER(ALL('Table'))` inside iterators like SUMX. This pattern forces the engine to materialize the entire table. Use CALCULATE with column-level filters instead.

### 6.3 Bidirectional Relationship Cost

Bidirectional cross-filtering on large tables (especially bridges) forces a full table scan on every query that filters through the relationship. Prefer single-direction defaults and activate bidirectional at runtime via CROSSFILTER only in measures that need it.

### 6.4 Unused Columns

Hidden columns with no references in measures, relationships, hierarchies, or sort-by configurations waste memory. Audit regularly and remove.

- Using MeasureKiller (third party tool, requires enterprise license to scan entire Fabric tenant) and Fabric Semantic Link Labs notebooks to check what fields are being used in reports.
  - Excel connections are still a blind spot, so be sure to summarize all proposed removals for business approval before implementing.
- Use the BPA rule `PERF_UNUSED_COLUMNS` to detect unused columns in terms of relationship or measure dependencies.
