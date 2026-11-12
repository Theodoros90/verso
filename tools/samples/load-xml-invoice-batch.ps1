<#
.SYNOPSIS
  Loads the "XML invoice batch" sample into a running Verso API: two templates, one workflow, all published.
  See docs/samples/xml-invoice-batch.md.

.PARAMETER ApiUrl     Base URL of the API (Development sign-in only: uses the synthetic user dev.admin).
.PARAMETER InboxRoot  Folder under which in / processed / failed are used by the workflow's Folder input step.
.PARAMETER Submit     Also submit tests/fixtures/xml-batch/invoices.xml to the workflow once (no folder needed).
.PARAMETER WrapXml    Write a JSON envelope of an XML file into the inbox ("in" folder), the way a sender must, then exit.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/samples/load-xml-invoice-batch.ps1 -Submit
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = 'http://localhost:5122',
    [string] $InboxRoot = 'D:\Verso\xml-batch',
    [switch] $Submit,
    [string] $WrapXml
)

$ErrorActionPreference = 'Stop'
$fixtures = Join-Path $PSScriptRoot '..\..\tests\fixtures\xml-batch' | Resolve-Path
$auth = @{ Authorization = 'Bearer dev:dev.admin' }

if ($WrapXml) {
    # Folder input reads JSON files only, so the XML travels as the "content" of a JSON file. Write under a temporary
    # name and rename when complete, so the Worker never picks up a half-written file.
    $in = Join-Path $InboxRoot 'in'
    New-Item -ItemType Directory -Force $in, (Join-Path $InboxRoot 'processed'), (Join-Path $InboxRoot 'failed') | Out-Null
    $name = [IO.Path]::GetFileNameWithoutExtension($WrapXml) + '-' + (Get-Date -Format 'yyyyMMddHHmmss')
    $json = @{ content = [string](Get-Content -Raw -Encoding UTF8 $WrapXml) } | ConvertTo-Json -Compress
    Set-Content -Encoding UTF8 -NoNewline -Path (Join-Path $in "$name.json.tmp") -Value $json
    Move-Item (Join-Path $in "$name.json.tmp") (Join-Path $in "$name.json")
    Write-Host "Wrote $(Join-Path $in "$name.json")"
    return
}

function Invoke-Verso([string] $Method, [string] $Path, $Body = $null, [string] $IfMatch = $null) {
    # Works in Windows PowerShell 5.1 and PowerShell 7. The ETag of the response is kept in $script:etag for the next If-Match.
    $headers = $auth.Clone()
    if ($IfMatch) { $headers['If-Match'] = $IfMatch }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json'; UseBasicParsing = $true }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 50 -Compress) }
    $response = Invoke-WebRequest @request
    $script:etag = [string]($response.Headers['ETag'] | Select-Object -First 1)
    if ($response.Content) { return ($response.Content | ConvertFrom-Json) }
}

function Publish-Template([string] $Key, [string] $Name, [string] $LayoutFile) {
    $template = Invoke-Verso Post '/api/v1/templates' @{ key = $Key; name = $Name }
    $null = Invoke-Verso Get "/api/v1/templates/$($template.id)/draft"
    $sample = Get-Content -Raw (Join-Path $fixtures 'sample-data.json') | ConvertFrom-Json
    $sample.PSObject.Properties.Remove('_comment')
    $body = @{
        pageSettings = @{ size = 'A4'; orientation = 'portrait'; marginsMm = @{ top = 18; right = 16; bottom = 18; left = 16 } }
        layout       = (Get-Content -Raw (Join-Path $fixtures $LayoutFile) | ConvertFrom-Json)
        dataSchema   = @{ type = 'object' }
        sampleData   = $sample
    }
    $null = Invoke-Verso Put "/api/v1/templates/$($template.id)/draft" $body $script:etag
    $null = Invoke-Verso Post "/api/v1/templates/$($template.id)/draft/publish" $null $script:etag
    return $template.id
}

$suffix = Get-Date -Format 'MMddHHmmss'
$invoiceId = Publish-Template "XMLB-INV-$suffix" 'XML batch invoice (2 pages)' 'invoice.layout.json'
$noticeId  = Publish-Template "XMLB-NTC-$suffix" 'XML batch VAT notice (2 pages)' 'notice.layout.json'

$text = (Get-Content -Raw (Join-Path $fixtures 'workflow.json')).Replace('__INVOICE_TEMPLATE_ID__', $invoiceId).Replace('__NOTICE_TEMPLATE_ID__', $noticeId)
$escaped = $InboxRoot.Replace('\', '\\')
$text = $text.Replace('D:\\Verso\\xml-batch', $escaped)
$definition = $text | ConvertFrom-Json
$definition.PSObject.Properties.Remove('_comment')

$key = "xml-invoice-batch-$suffix"
$workflow = Invoke-Verso Post '/api/v1/workflows' @{ key = $key; name = 'XML invoice batch'; description = 'Sample: XML file to invoice PDFs with routing' }
$null = Invoke-Verso Get "/api/v1/workflows/$($workflow.id)/draft"
$null = Invoke-Verso Put "/api/v1/workflows/$($workflow.id)/draft" @{ definition = $definition } $script:etag
$published = Invoke-Verso Post "/api/v1/workflows/$($workflow.id)/draft/publish" $null $script:etag
Write-Host "Published workflow '$key' (version $($published.versionNumber)); templates $invoiceId and $noticeId."
Write-Host "Folder input: $InboxRoot\in  (create the folders, give the Worker's service account Modify access)."

if ($Submit) {
    # [string]: in Windows PowerShell 5.1 Get-Content -Raw carries extra properties that ConvertTo-Json would serialize as an object.
    $xml = [string](Get-Content -Raw -Encoding UTF8 (Join-Path $fixtures 'invoices.xml'))
    $run = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/v1/workflows/$key/executions" -ContentType application/json `
        -Headers ($auth + @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }) -Body (@{ data = @{ content = $xml } } | ConvertTo-Json -Compress)
    Write-Host "Submitted run $($run.executionId). The third invoice (over 10,000) waits for an approval: decide it in the run view, or POST /api/v1/executions/<id>/approvals/approve with { decision, iteration: 3 }."
}
