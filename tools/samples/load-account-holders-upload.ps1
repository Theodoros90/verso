<#
.SYNOPSIS
  Loads the "account holders by upload" sample into a running Verso API: one template and one workflow (File upload start step),
  both published. See docs/samples/file-upload-account-holders.md.

.PARAMETER ApiUrl  Base URL of the API (Development sign-in only: uses the synthetic user dev.admin).
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5122'
)

$ErrorActionPreference = 'Stop'
$fixtures = Join-Path $PSScriptRoot '..\..\tests\fixtures\account-holders' | Resolve-Path
$auth = @{ Authorization = 'Bearer dev:dev.admin' }

function Invoke-Verso([string] $Method, [string] $Path, $Body = $null, [string] $IfMatch = $null) {
    $headers = $auth.Clone()
    if ($IfMatch) { $headers['If-Match'] = $IfMatch }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json'; UseBasicParsing = $true }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 50 -Compress) }
    $response = Invoke-WebRequest @request
    $script:etag = [string]($response.Headers['ETag'] | Select-Object -First 1)
    if ($response.Content) { return ($response.Content | ConvertFrom-Json) }
}

$suffix = Get-Date -Format 'MMddHHmmss'
$template = Invoke-Verso Post '/api/v1/templates' @{ key = "ACC-$suffix"; name = 'Account holder summary' }
$null = Invoke-Verso Get "/api/v1/templates/$($template.id)/draft"
$sample = Get-Content -Raw (Join-Path $fixtures 'sample-data.json') | ConvertFrom-Json
$body = @{
    pageSettings = @{ size = 'A4'; orientation = 'portrait'; marginsMm = @{ top = 18; right = 16; bottom = 18; left = 16 } }
    layout       = (Get-Content -Raw (Join-Path $fixtures 'layout.json') | ConvertFrom-Json)
    dataSchema   = @{ type = 'object' }
    sampleData   = $sample
}
$null = Invoke-Verso Put "/api/v1/templates/$($template.id)/draft" $body $script:etag
$null = Invoke-Verso Post "/api/v1/templates/$($template.id)/draft/publish" $null $script:etag

$text = (Get-Content -Raw (Join-Path $fixtures 'workflow-upload.json')).Replace('__TEMPLATE_ID__', $template.id)
$definition = $text | ConvertFrom-Json
$definition.PSObject.Properties.Remove('_comment')

$key = "account-upload-$suffix"
$workflow = Invoke-Verso Post '/api/v1/workflows' @{ key = $key; name = "Account holder PDFs (upload $suffix)"; description = 'Sample: upload an XML of account holders, a PDF per AccountID starting with C, archived' }
$null = Invoke-Verso Get "/api/v1/workflows/$($workflow.id)/draft"
$null = Invoke-Verso Put "/api/v1/workflows/$($workflow.id)/draft" @{ definition = $definition } $script:etag
$published = Invoke-Verso Post "/api/v1/workflows/$($workflow.id)/draft/publish" $null $script:etag
Write-Host "Published workflow '$key' (version $($published.versionNumber)) named 'Account holder PDFs (upload $suffix)'; template $($template.id)."
Write-Host "Next: sign in as the Operator, open Workflows, and use the card's menu, Upload file..., with tests\fixtures\account-holders\Account.xml"
