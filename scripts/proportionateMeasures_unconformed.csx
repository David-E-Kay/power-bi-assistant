// Tabular Editor Advanced Scripting
// Finds measures with inline proportionate logic that reference
// 'Properties'[Ownership Proportionate Perc] but do NOT call Proportionate(...)

var ownershipRefRx = new System.Text.RegularExpressions.Regex(
    @"'Properties'\s*\[\s*Ownership Proportionate Perc\s*\]",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase
);

var proportionateFuncRx = new System.Text.RegularExpressions.Regex(
    @"\bProportionate\s*\(",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase
);

var sumxRx = new System.Text.RegularExpressions.Regex(
    @"\bSUMX\s*\(",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase
);

var averagexRx = new System.Text.RegularExpressions.Regex(
    @"\bAVERAGEX\s*\(",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase
);

var multiplyOwnershipRx = new System.Text.RegularExpressions.Regex(
    @"\*\s*'Properties'\s*\[\s*Ownership Proportionate Perc\s*\]",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase
);

var ifOwnershipRx = new System.Text.RegularExpressions.Regex(
    @"\bIF\s*\(.*'Properties'\s*\[\s*Ownership Proportionate Perc\s*\]",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline
);

System.Func<string, string> stripComments = dax =>
{
    if (string.IsNullOrWhiteSpace(dax)) return string.Empty;

    // Remove block comments: /* ... */
    dax = System.Text.RegularExpressions.Regex.Replace(
        dax,
        @"/\*.*?\*/",
        "",
        System.Text.RegularExpressions.RegexOptions.Singleline
    );

    // Remove line comments: // ...
    var lines = dax.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None);
    var cleaned = new System.Text.StringBuilder();

    foreach (var line in lines)
    {
        var idx = line.IndexOf("//");
        cleaned.AppendLine(idx >= 0 ? line.Substring(0, idx) : line);
    }

    return cleaned.ToString();
};

var results = new System.Collections.Generic.List<string>();

foreach (var m in Model.AllMeasures.OrderBy(x => x.Table.Name).ThenBy(x => x.Name))
{
    var dax = stripComments(m.Expression);

    if (string.IsNullOrWhiteSpace(dax))
        continue;

    var hasOwnershipRef = ownershipRefRx.IsMatch(dax);
    var callsProportionateFunction = proportionateFuncRx.IsMatch(dax);

    // Narrow to likely inline proportionate math patterns
    var hasInlineMathPattern =
        sumxRx.IsMatch(dax) ||
        averagexRx.IsMatch(dax) ||
        multiplyOwnershipRx.IsMatch(dax) ||
        ifOwnershipRx.IsMatch(dax);

    if (hasOwnershipRef && !callsProportionateFunction && hasInlineMathPattern)
    {
        var reasons = new System.Collections.Generic.List<string>();
        if (sumxRx.IsMatch(dax)) reasons.Add("SUMX");
        if (averagexRx.IsMatch(dax)) reasons.Add("AVERAGEX");
        if (multiplyOwnershipRx.IsMatch(dax)) reasons.Add("* Ownership %");
        if (ifOwnershipRx.IsMatch(dax)) reasons.Add("IF/toggle logic");

        results.Add(
            m.Table.Name + "[" + m.Name + "]"
            + "  |  "
            + string.Join(", ", reasons.Distinct())
        );
    }
}

var sb = new System.Text.StringBuilder();
sb.AppendLine("Measures with inline proportionate logic using 'Properties'[Ownership Proportionate Perc] and NOT calling Proportionate(...):");
sb.AppendLine("Count: " + results.Count);
sb.AppendLine();

foreach (var r in results)
{
    sb.AppendLine(r);
}

var output = sb.ToString();

var filePath = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(),
    "TE_InlineProportionateMeasureAudit_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
);

System.IO.File.WriteAllText(filePath, output);

try
{
    System.Windows.Forms.Clipboard.SetText(output);
}
catch
{
    // Clipboard can fail in some environments; ignore.
}

System.Windows.Forms.MessageBox.Show(
    output + "\n\nSaved to:\n" + filePath + "\n\nThe results were also copied to the clipboard when possible.",
    "Inline Proportionate Measure Audit"
);