<#
.SYNOPSIS
  Verso's CI pipeline as one script (M9): everything a CI server must run, in order, failing on the first error.
  When the bank chooses its CI system, its pipeline file just runs this script on a Windows agent.

.DESCRIPTION
  1. Prerequisites: .NET SDK 10, Node >= 22.22, SQL Server (LocalDB or VERSO_TEST_SQL_SERVER), Chromium for Playwright.
  2. Restore tools and packages; build the solution in Release with warnings as errors.
  3. Frontend: npm ci, typecheck, unit tests, production build.
  4. Backend tests, one test project at a time: unit, API integration (LocalDB), PDF (real Chromium), and the
     browser E2E suite (real API + Worker processes, built SPA, Chromium). Projects run one after another because the
     PDF timing test (p95 < 3 s) is sensitive to other suites competing for the CPU.
  5. Vulnerability check of NuGet and npm packages (production dependencies).

.PARAMETER NoInstall
  Skip `npm ci` (keep the existing node_modules), e.g. on a developer machine with the dev server running.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/ci.ps1
#>
param([switch]$NoInstall)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src\verso-web'
$results = [System.Collections.Generic.List[object]]::new()

function Step([string]$name, [scriptblock]$action) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $action
        if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        $results.Add([pscustomobject]@{ Step = $name; Result = 'passed'; Seconds = [int]$watch.Elapsed.TotalSeconds })
    }
    catch {
        $results.Add([pscustomobject]@{ Step = $name; Result = "FAILED ($_)"; Seconds = [int]$watch.Elapsed.TotalSeconds })
        $results | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host "CI failed at: $name" -ForegroundColor Red
        exit 1
    }
}

Push-Location $root
try {
    Step 'Prerequisites' {
        $sdk = (dotnet --version); if (-not $sdk.StartsWith('10.')) { throw ".NET SDK 10 required, found $sdk" }
        $node = (node --version).TrimStart('v'); if ([version]$node -lt [version]'22.22.0') { throw "Node >= 22.22 required, found $node" }
        $browsers = if ($env:PLAYWRIGHT_BROWSERS_PATH) { $env:PLAYWRIGHT_BROWSERS_PATH } else { Join-Path $env:LOCALAPPDATA 'ms-playwright' }
        if (-not (Get-ChildItem $browsers -Filter 'chromium_headless_shell-*' -Directory -ErrorAction SilentlyContinue)) {
            throw "Chromium for Playwright not found in $browsers (docs/setup.md, step 2)"
        }
        $global:LASTEXITCODE = 0
    }
    Step 'Restore and build (Release)' {
        dotnet tool restore
        if ($LASTEXITCODE -eq 0) { dotnet build Verso.slnx -c Release -nr:false }
    }
    Step 'Frontend: install' {
        Push-Location $web
        try { if ($NoInstall) { Write-Host 'skipped (-NoInstall)'; $global:LASTEXITCODE = 0 } else { npm ci } } finally { Pop-Location }
    }
    Step 'Frontend: typecheck' { Push-Location $web; try { npm run typecheck } finally { Pop-Location } }
    Step 'Frontend: unit tests' { Push-Location $web; try { npm test } finally { Pop-Location } }
    Step 'Frontend: production build' { Push-Location $web; try { npm run build } finally { Pop-Location } }
    Step 'Backend: unit tests' { dotnet test tests/Verso.UnitTests -c Release --no-build }
    Step 'Backend: API integration tests (LocalDB)' { dotnet test tests/Verso.Api.Tests -c Release --no-build }
    Step 'Backend: PDF tests (Chromium)' { dotnet test tests/Verso.Pdf.Tests -c Release --no-build }
    Step 'E2E: the 7 MVP steps in the browser' { dotnet test tests/Verso.E2E.Tests -c Release --no-build }
    Step 'Vulnerable packages' {
        $nuget = dotnet list Verso.slnx package --vulnerable --include-transitive 2>&1 | Out-String
        if ($nuget -match 'has the following vulnerable packages') { Write-Host $nuget; throw 'vulnerable NuGet packages' }
        Push-Location $web
        try { npm audit --omit=dev --audit-level=high } finally { Pop-Location }
    }
}
finally {
    Pop-Location
}

$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host 'CI passed.' -ForegroundColor Green
