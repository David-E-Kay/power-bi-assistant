# PowerShell Script: BPA Analysis on local .pbix file via TE2 CLI
# Uses Microsoft's standard BPA rules

Write-Host "`n$('='*70)" -ForegroundColor Cyan
Write-Host "Power BI Best Practice Analyzer - TE2 CLI (Local File)" -ForegroundColor Cyan
Write-Host "$('='*70)`n" -ForegroundColor Cyan

# Model file path
$pbixPath = "C:\Users\dkay\Desktop\Maintenance and Construction Refactor v2.pbix"

# Verify file exists
if (-not (Test-Path $pbixPath)) {
    Write-Host "ERROR: Model file not found at $pbixPath" -ForegroundColor Red
    exit 1
}

Write-Host "Model file: $pbixPath" -ForegroundColor Green
Write-Host "File size: $([math]::Round((Get-Item $pbixPath).Length / 1GB, 2))GB" -ForegroundColor Gray
Write-Host ""

# TE2 path
$te2Path = "C:\Program Files (x86)\Tabular Editor\TabularEditor.exe"
$bpaRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/BPARules.json"

if (-not (Test-Path $te2Path)) {
    Write-Host "ERROR: TE2 not found at $te2Path" -ForegroundColor Red
    exit 1
}

Write-Host "Running BPA analysis..." -ForegroundColor Yellow
Write-Host "  File: $pbixPath" -ForegroundColor Gray
Write-Host "  Rules: Microsoft Analysis Services Best Practice Rules" -ForegroundColor Gray
Write-Host "  Output format: Azure DevOps (verbose)" -ForegroundColor Gray
Write-Host ""
Write-Host "Note: This may take a minute or two for large models..." -ForegroundColor Yellow
Write-Host ""

# Run TE2 BPA on the .pbix file
# TabularEditor.exe <file.pbix> -A rules.json -V
try {
    & $te2Path "$pbixPath" -A "$bpaRulesUrl" -V 2>&1 | Tee-Object -Variable te2Output

    Write-Host ""
    Write-Host "$('='*70)" -ForegroundColor Green
    Write-Host "BPA ANALYSIS RESULTS" -ForegroundColor Green
    Write-Host "$('='*70)" -ForegroundColor Green
    Write-Host ""

    # Show output
    $te2Output | ForEach-Object {
        if ($_ -match "error|warning|Critical|High|Medium") {
            Write-Host $_ -ForegroundColor Yellow
        } else {
            Write-Host $_
        }
    }

    Write-Host ""
    Write-Host "$('='*70)`n" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: $PSItem" -ForegroundColor Red
    exit 1
}
