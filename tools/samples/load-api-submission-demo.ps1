<#
.SYNOPSIS
  Demo for the "API submission" trigger. Publishes an invoice template and a workflow (API submission -> Generate PDF -> Archive)
  into a running Verso API, submits the synthetic invoice through POST /api/v1/workflows/{key}/executions and shows the run.
  See docs/samples/api-submission-demo.md.

.PARAMETER ApiUrl   Base URL of the API (Development sign-in only: uses the synthetic users dev.admin and dev.operator).
.PARAMETER Broken   Submit tests/fixtures/invoice/broken.json instead of the good invoice.
.PARAMETER NoSubmit Only publish the template and workflow; do not submit.
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5122',
    [switch] $Broken,
    [switch] $NoSubmit
)

$ErrorActionPreference = 'Stop'
$fixtures = Join-Path $PSScriptRoot '..\..\tests\fixtures\invoice' | Resolve-Path

function Invoke-Verso([string] $Method, [string] $Path, $Body = $null, [string] $IfMatch = $null, [string] $User = 'dev.admin', [hashtable] $Extra = @{}) {
    $headers = @{ Authorization = "Bearer dev:$User" } + $Extra
    if ($IfMatch) { $headers['If-Match'] = $IfMatch }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json'; UseBasicParsing = $true }
    # UTF-8 bytes: Windows PowerShell 5.1 would send a string body as Latin-1, and the layout has non-ASCII text.
    if ($null -ne $Body) { $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 50 -Compress)) }
    try { $response = Invoke-WebRequest @request }
    catch {
        $detail = if ($_.Exception.Response) { (New-Object IO.StreamReader $_.Exception.Response.GetResponseStream()).ReadToEnd() } else { $_.Exception.Message }
        throw "$Method $Path failed: $detail"
    }
    $script:etag = [string]($response.Headers['ETag'] | Select-Object -First 1)
    if ($response.Content) { return ($response.Content | ConvertFrom-Json) }
}

$suffix = Get-Date -Format 'MMddHHmmss'

# 1. A template with a published version.
$template = Invoke-Verso Post '/api/v1/templates' @{ key = "DEMO-INV-$suffix"; name = 'Demo invoice' }
$null = Invoke-Verso Get "/api/v1/templates/$($template.id)/draft"
$sample = Get-Content -Raw -Encoding UTF8 (Join-Path $fixtures 'sample.json') | ConvertFrom-Json
$sample.PSObject.Properties.Remove('_comment')
$body = @{
    pageSettings = @{ size = 'A4'; orientation = 'portrait'; marginsMm = @{ top = 18; right = 16; bottom = 18; left = 16 } }
    layout       = (Get-Content -Raw -Encoding UTF8 (Join-Path $fixtures 'layout.json') | ConvertFrom-Json)
    dataSchema   = @{ type = 'object' }
    sampleData   = $sample
}
$null = Invoke-Verso Put "/api/v1/templates/$($template.id)/draft" $body $script:etag
$null = Invoke-Verso Post "/api/v1/templates/$($template.id)/draft/publish" $null $script:etag

# 2. A workflow that starts with the API submission step.
$definition = @{
    nodes = @(
        @{ id = 'trigger'; type = 'api-trigger'; title = 'API submission'; position = @{ x = 0; y = 80 }
           config = @{ path = '/hooks/demo-invoice'; auth = 'none' } },
        @{ id = 'pdf'; type = 'generate-pdf'; title = 'Invoice PDF'; position = @{ x = 260; y = 80 }
           config = @{ templateId = $template.id; templateVersion = 'latest-published'; fileName = '{{invoice.number}}.pdf' } },
        @{ id = 'archive'; type = 'archive'; title = 'Archive'; position = @{ x = 520; y = 80 }
           config = @{ folderPattern = 'demo/{{customer.name}}'; retention = '7y'
                       metadata = @(@{ key = 'Invoice'; value = '{{invoice.number}}' }, @{ key = 'Customer'; value = '{{customer.name}}' }) } }
    )
    edges = @(
        @{ id = 'e1'; source = 'trigger'; sourcePort = 'out'; target = 'pdf'; targetPort = 'in' },
        @{ id = 'e2'; source = 'pdf'; sourcePort = 'success'; target = 'archive'; targetPort = 'in' }
    )
}
$key = "api-submission-demo-$suffix"
$workflow = Invoke-Verso Post '/api/v1/workflows' @{ key = $key; name = 'API submission demo'; description = 'Demo: a system submits an invoice as JSON, a PDF is made and archived' }
$null = Invoke-Verso Get "/api/v1/workflows/$($workflow.id)/draft"
$null = Invoke-Verso Put "/api/v1/workflows/$($workflow.id)/draft" @{ definition = $definition } $script:etag
$published = Invoke-Verso Post "/api/v1/workflows/$($workflow.id)/draft/publish" $null $script:etag
Write-Host "Published workflow '$key' (version $($published.versionNumber)); template $($template.id)."

if ($NoSubmit) { return }

# 3. Submit the data the way an outside system would (an Operator), then wait for the Worker.
$file = if ($Broken) { 'broken.json' } else { 'sample.json' }
$data = Get-Content -Raw -Encoding UTF8 (Join-Path $fixtures $file) | ConvertFrom-Json
$data.PSObject.Properties.Remove('_comment')
$run = Invoke-Verso Post "/api/v1/workflows/$key/executions" @{ data = $data } -User 'dev.operator' -Extra @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
Write-Host "Submitted $file; execution $($run.executionId)."

$status = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep 1
    $status = Invoke-Verso Get "/api/v1/executions/$($run.executionId)" -User 'dev.operator'
    if ($status.status -in 'Succeeded', 'Rejected', 'Failed') { break }
}
Write-Host "Status: $($status.status)"
$status | ConvertTo-Json -Depth 6
