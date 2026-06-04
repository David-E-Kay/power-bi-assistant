# PowerShell Script: BPA Analysis via TE2 CLI
# Auto-detects running Power BI Desktop instances and runs Best Practice Analyzer
# Uses Microsoft's standard BPA rules from GitHub

Write-Host "`n$('='*70)" -ForegroundColor Cyan
Write-Host "Power BI Best Practice Analyzer - TE2 CLI" -ForegroundColor Cyan
Write-Host "$('='*70)`n" -ForegroundColor Cyan

# Find all Power BI Desktop XMLA processes
$pbiProcesses = Get-Process msmdsrv -ErrorAction SilentlyContinue
if (-not $pbiProcesses) {
    Write-Host "ERROR: No Power BI Desktop instances found running" -ForegroundColor Red
    exit 1
}

Write-Host "Auto-detecting Power BI Desktop instances..." -ForegroundColor Yellow

# Get all listening ports for PBI processes
$modelPorts = @()
$netstatOutput = netstat -ano

foreach ($process in $pbiProcesses) {
    $processId = $process.Id

    foreach ($line in $netstatOutput) {
        if ($line -match "TCP\s+127\.0\.0\.1:(\d+)\s+.*LISTENING\s+$processId") {
            $port = [int]$matches[1]
            if ($modelPorts -notcontains $port) {
                $modelPorts += $port
            }
        }
    }
}

if ($modelPorts.Count -eq 0) {
    Write-Host "ERROR: Could not find any listening ports for Power BI instances" -ForegroundColor Red
    exit 1
}

# Sort ports descending (most recent first)
$modelPorts = $modelPorts | Sort-Object -Descending

# Display found instances
Write-Host "Found $($modelPorts.Count) Power BI instance(s):`n" -ForegroundColor Green
for ($i = 0; $i -lt $modelPorts.Count; $i++) {
    Write-Host "  [$($i+1)] localhost:$($modelPorts[$i])" -ForegroundColor Cyan
}

# Select instance to analyze
$selectedPort = $null
if ($modelPorts.Count -eq 1) {
    $selectedPort = $modelPorts[0]
    Write-Host "`nOnly one instance found. Using: localhost:$selectedPort`n" -ForegroundColor Green
} else {
    Write-Host "`nWhich instance do you want to analyze? (Enter number 1-$($modelPorts.Count))" -ForegroundColor Yellow
    $choice = Read-Host "Selection"

    if (-not ($choice -match '^\d+$') -or [int]$choice -lt 1 -or [int]$choice -gt $modelPorts.Count) {
        Write-Host "Invalid selection" -ForegroundColor Red
        exit 1
    }
    $selectedIndex = [int]$choice - 1
    $selectedPort = $modelPorts[$selectedIndex]
    Write-Host "Selected: localhost:$selectedPort`n" -ForegroundColor Green
}

# TE2 paths
$te2Path = "C:\Program Files (x86)\Tabular Editor\TabularEditor.exe"
$bpaRulesUrl = "https://raw.githubusercontent.com/microsoft/Analysis-Services/master/BestPracticeRules/BPARules.json"
$outputFile = "C:\Users\dkay\Desktop\Claude Projects\Power BI Assistant\output\bpa-results.txt"

if (-not (Test-Path $te2Path)) {
    Write-Host "ERROR: TE2 not found at $te2Path" -ForegroundColor Red
    exit 1
}

# Delete old output
if (Test-Path $outputFile) {
    Remove-Item $outputFile -Force
}

Write-Host "Running BPA analysis..." -ForegroundColor Yellow
Write-Host "  Instance: localhost:$selectedPort" -ForegroundColor Gray
Write-Host "  Rules: Microsoft Analysis Services Best Practice Rules" -ForegroundColor Gray
Write-Host ""

# Run TE2 BPA analysis
# For Power BI Desktop: TabularEditor.exe localhost:PORT -A rules.json -V
# The -V flag adds Azure DevOps logging format (good for visibility)
try {
    # Run BPA with verbose output
    & $te2Path "localhost:$selectedPort" -A "$bpaRulesUrl" -V 2>&1 | Tee-Object -Variable te2Output

    Write-Host "`n$('='*70)" -ForegroundColor Green
    Write-Host "BPA ANALYSIS COMPLETE" -ForegroundColor Green
    Write-Host "$('='*70)" -ForegroundColor Green
    Write-Host ""

    # Display the output
    Write-Host $te2Output

    Write-Host "`n$('='*70)`n" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: $PSItem" -ForegroundColor Red
    exit 1
}
