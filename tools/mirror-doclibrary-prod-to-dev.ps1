<#
.SYNOPSIS
    §768 — mirror the PROD DocLibrary tree (folders + files) into the DEV root.

.DESCRIPTION
    Operator 2026-08-02: *"you create the structure so it has same folder names as prod - just under
    the dev root"* and *"must mirror existin from prod to dev so we can test sharepoint integration"*.

    DEV had NO usable SharePoint tree, which is why every §767 change had to be verified in
    production. This makes DEV a real environment: the same folder NAMES as PROD, so a path differs
    between environments **only in its root** — which is what lets the root be one configuration
    value instead of a path per service.

    Content is mirrored too, not just structure. An empty tree cannot exercise anything: the graphics
    sweep needs the template and the speaker photos, the venue pages need their images.

    🔒 ONE-WAY, PROD → DEV. The destination is asserted to be under the DEV root before any write.
    Nothing is ever written to the ELDK 2027 tree by this script.

.NOTES
    Re-runnable: existing folders are reused, existing files are skipped unless -Force.
    Auth: Connect-AzAccount (reads the app's SharePoint creds from the prod function app).
#>
param(
    [switch]$WhatIf,
    [switch]$Force,          # re-copy files that already exist in DEV
    [string[]]$Only          # limit to top-level folders, e.g. -Only Speakers,Event
)

$ErrorActionPreference = 'Stop'

$SRC_ROOT = 'General/Events/ELDK 2027/EventHub'
$DST_ROOT = 'General/DEVELOPMENT/EventHub'
$DRIVE    = 'b!MTgLaVLcCEeUbmKcwBB1bamztWjCTs9BtPTBT9uNVYxl351rpj2ZQoKOUS63S9Gs'

# 🔒 Guard: refuse to run if the destination is not the DEV root. A typo here would write into the
# live event tree, which is the one outcome this whole exercise exists to prevent.
if ($DST_ROOT -notmatch '^General/DEVELOPMENT/') {
    throw "Destination root '$DST_ROOT' is not under General/DEVELOPMENT/ — refusing to run."
}

$s = @{}
(Get-AzWebApp -ResourceGroupName 'rg-eldk27hub-prod' -Name 'eldk27hub-fn-prodpdrq').SiteConfig.AppSettings |
    ForEach-Object { $s[$_.Name] = $_.Value }
$token = (Invoke-RestMethod -Method Post `
    -Uri "https://login.microsoftonline.com/$($s['SharePoint__TenantId'])/oauth2/v2.0/token" `
    -Body @{
        client_id     = $s['SharePoint__ClientId']
        client_secret = $s['SharePoint__ClientSecret']
        scope         = 'https://graph.microsoft.com/.default'
        grant_type    = 'client_credentials'
    }).access_token
$H = @{ Authorization = "Bearer $token" }

function Esc($path) { ($path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/' }

function Get-Item-ByPath($path) {
    try { Invoke-RestMethod -Headers $H -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/root:/$(Esc $path)" }
    catch { $null }
}

function Get-Children($itemId) {
    (Invoke-RestMethod -Headers $H -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/items/$itemId/children?`$top=200").value
}

function New-Folder($parentId, $name) {
    $body = @{ name = $name; folder = @{}; '@microsoft.graph.conflictBehavior' = 'fail' } | ConvertTo-Json
    try {
        Invoke-RestMethod -Headers $H -Method Post -ContentType 'application/json' `
            -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/items/$parentId/children" -Body $body
    } catch {
        # Already there (a concurrent run, or -WhatIf drift). Re-read rather than fail the mirror.
        (Get-Children $parentId) | Where-Object { $_.name -eq $name -and $_.folder } | Select-Object -First 1
    }
}

$script:Folders = 0; $script:Copied = 0; $script:Skipped = 0

function Mirror($srcId, $dstId, $label) {
    $srcKids = Get-Children $srcId
    $dstKids = Get-Children $dstId

    foreach ($k in ($srcKids | Sort-Object name)) {
        if ($k.folder) {
            $existing = $dstKids | Where-Object { $_.name -eq $k.name -and $_.folder } | Select-Object -First 1
            if ($existing) {
                Write-Host "   = $label/$($k.name)" -ForegroundColor DarkGray
            } elseif ($WhatIf) {
                Write-Host "   + $label/$($k.name)  (WHATIF)" -ForegroundColor Yellow
                continue          # cannot recurse into a folder we did not create
            } else {
                $existing = New-Folder $dstId $k.name
                $script:Folders++
                Write-Host "   + $label/$($k.name)" -ForegroundColor Green
            }
            Mirror $k.id $existing.id "$label/$($k.name)"
        }
        else {
            $dup = $dstKids | Where-Object { $_.name -eq $k.name -and -not $_.folder } | Select-Object -First 1
            if ($dup -and -not $Force) { $script:Skipped++; continue }
            if ($WhatIf) { Write-Host "     copy $label/$($k.name)  (WHATIF)" -ForegroundColor Yellow; continue }

            # Server-side copy — the bytes never travel through this machine.
            $body = @{ parentReference = @{ driveId = $DRIVE; id = $dstId }; name = $k.name } | ConvertTo-Json
            try {
                Invoke-WebRequest -Headers $H -Method Post -ContentType 'application/json' `
                    -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/items/$($k.id)/copy" `
                    -Body $body -UseBasicParsing | Out-Null
                $script:Copied++
                Write-Host "     -> $label/$($k.name)" -ForegroundColor Cyan
            } catch {
                Write-Host "     !! $label/$($k.name) : $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
}

$src = Get-Item-ByPath $SRC_ROOT
if (-not $src) { throw "Source root not found: $SRC_ROOT" }
$dst = Get-Item-ByPath $DST_ROOT
if (-not $dst) { throw "DEV root not found: $DST_ROOT — create it in SharePoint first." }

Write-Host "PROD $SRC_ROOT  ->  DEV $DST_ROOT" -ForegroundColor Cyan
if ($Only) {
    foreach ($name in $Only) {
        $sub = (Get-Children $src.id) | Where-Object { $_.name -eq $name -and $_.folder }
        if (-not $sub) { Write-Host "   (no such top-level folder: $name)" -ForegroundColor Red; continue }
        $existing = (Get-Children $dst.id) | Where-Object { $_.name -eq $name -and $_.folder }
        if (-not $existing -and -not $WhatIf) { $existing = New-Folder $dst.id $name; $script:Folders++ }
        if ($existing) { Mirror $sub.id $existing.id "/$name" }
    }
} else {
    Mirror $src.id $dst.id ''
}

Write-Host "`nfolders created: $script:Folders · files copied: $script:Copied · already present: $script:Skipped" -ForegroundColor Cyan
Write-Host "Copies are server-side and ASYNC — re-list in a few seconds to confirm counts." -ForegroundColor DarkGray
