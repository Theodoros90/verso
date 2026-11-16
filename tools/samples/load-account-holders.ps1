<#
.SYNOPSIS
  Loads the "account holders" sample into a running Verso API: one template and one workflow, both published.
  See docs/samples/account-holders.md.

.PARAMETER ApiUrl     Base URL of the API (Development sign-in only: uses the synthetic user dev.admin).
.PARAMETER InputRoot  Folder the workflow's Folder input step watches. Processed and Failed are created beside it.
.PARAMETER WrapXml    Write a JSON envelope of an XML file into the input folder, the way a sender must, then exit.
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5122',
    [string] $InputRoot = 'C:\LN\DocGen\test',
    [string] $WrapXml
)

$ErrorActionPreference = 'Stop'
$fixtures = Join-Path $PSScriptRoot '..\..\tests\fixtures\account-holders' | Resolve-Path
$auth = @{ Authorization = 'Bearer dev:dev.admin' }

if ($WrapXml) {
    # Folder input reads JSON files only, so the XML travels as the "content" of a JSON file; write under a temporary name, then rename.
    $in = Join-Path $InputRoot 'Input'
    New-Item -ItemType Directory -Force $in, (Join-Path $InputRoot 'Processed'), (Join-Path $InputRoot 'Failed') | Out-Null
    $name = [IO.Path]::GetFileNameWithoutExtension($WrapXml) + '-' + (Get-Date -Format 'yyyyMMddHHmmss')
    # Trim: an XML declaration must be the first thing in the text. Write UTF-8 without a BOM (the Worker's JSON reader rejects one;
    # Set-Content -Encoding UTF8 in Windows PowerShell 5.1 adds it).
    $json = @{ content = ([string](Get-Content -Raw -Encoding UTF8 $WrapXml)).Trim() } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $in "$name.json.tmp"), $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item (Join-Path $in "$name.json.tmp") (Join-Path $in "$name.json")
    Write-Host "Wrote $(Join-Path $in "$name.json")"
    return
}

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

$text = (Get-Content -Raw (Join-Path $fixtures 'workflow.json')).Replace('__TEMPLATE_ID__', $template.id)
$text = $text.Replace('C:\\LN\\DocGen\\test', $InputRoot.Replace('\', '\\'))
$definition = $text | ConvertFrom-Json
$definition.PSObject.Properties.Remove('_comment')

$key = "account-holders-$suffix"
$workflow = Invoke-Verso Post '/api/v1/workflows' @{ key = $key; name = 'Account holder PDFs'; description = 'Sample: XML of account holders, a PDF per AccountID starting with C, archived' }
$null = Invoke-Verso Get "/api/v1/workflows/$($workflow.id)/draft"
$null = Invoke-Verso Put "/api/v1/workflows/$($workflow.id)/draft" @{ definition = $definition } $script:etag
$published = Invoke-Verso Post "/api/v1/workflows/$($workflow.id)/draft/publish" $null $script:etag
Write-Host "Published workflow '$key' (version $($published.versionNumber)); template $($template.id)."
Write-Host "Next: load-account-holders.ps1 -WrapXml C:\LN\DocGen\test\Account.xml   (drops the JSON envelope into $InputRoot\Input)"
