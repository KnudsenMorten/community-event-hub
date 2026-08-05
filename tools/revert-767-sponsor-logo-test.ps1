<#
.SYNOPSIS
    §767.5 — REMOVE the temporary sponsor-logo TEST data from PROD.

.DESCRIPTION
    The operator authorised a one-off test on 2026-08-02 to prove the §767 sponsor path end to end,
    on the explicit condition that it be reverted:

        "it is a temporary test. i will delete them once tested. in real prod we use only the
         builtin code path ... these tests are test only and must be reverted once i confirm it works"

    🔒 THE REAL SOURCE IS THE HUB'S OWN SPONSOR UPLOAD FORM. Nothing here is a supported way to
    populate `Logo-SoMeBranding`; the logos were copied in by hand purely so the sweep had something
    to compose. This script removes what that test created, in the order that leaves nothing orphaned.

    Three layers were created, and ALL THREE must go or the next sweep simply rebuilds:
      1. the 11 hand-placed logo files in Logo-SoMeBranding  (the INPUT),
      2. the generated artwork in Graphics/Sponsors + Graphics/SponsorCategories  (the OUTPUT),
      3. the GraphicAsset rows pointing at it  (the RECORD).

    ⚠️ Deleting only the logos leaves 11 sponsor cards live with no input behind them. Deleting only
    the files leaves DB rows whose SharePointUrl 404s. Run the whole thing.

.NOTES
    Requires: Connect-AzAccount (for the app settings) + `az` for a SQL token — the two auth paths,
    see CLAUDE.md. Nothing is written to disk.
#>
param(
    [switch]$WhatIf,
    [switch]$SkipDatabase
)

$ErrorActionPreference = 'Stop'

$RG        = 'rg-eldk27hub-prod'
$FN        = 'eldk27hub-fn-prodpdrq'
$DRIVE     = 'b!MTgLaVLcCEeUbmKcwBB1bamztWjCTs9BtPTBT9uNVYxl351rpj2ZQoKOUS63S9Gs'
$SQLSERVER = 'eldk27hub-sql-prodpdrq.database.windows.net'
$SQLDB     = 'eldk27hub-db'

# THE INPUT — every file this test placed. Company names are the SponsorInfos.CompanyName values
# the matcher keys on, so this list doubles as the record of what was uploaded.
$LogoFolder = 'General/Events/ELDK 2027/EventHub/Sponsors/Logo-SoMeBranding'
$TestLogos = @(
    'SoMeBrandingLogo_Nerdio_v1.png'
    'SoMeBrandingLogo_Nerdio_v2.png'                                    # the transparent-bg fix
    'SoMeBrandingLogo_twoday Danmark_v1.jpg'
    'SoMeBrandingLogo_APENTO_v1.png'
    'SoMeBrandingLogo_System Center Dudes_v1.png'
    'SoMeBrandingLogo_Admin By Request_v1.png'
    'SoMeBrandingLogo_Patch My PC_v1.png'
    'SoMeBrandingLogo_Robopack - empowered by SOFTWARECENTRAL_v1.png'
    'SoMeBrandingLogo_GLOBETEAM_v1.jpg'
    'SoMeBrandingLogo_2linkIT_v1.jpg'
    'SoMeBrandingLogo_Identity Stack_v1.png'
    'SoMeBrandingLogo_Surveil_v1.png'
)

# THE OUTPUT — folders whose sponsor content exists ONLY because of the test.
# 🔒 Sessions/ and SpeakerTracks/ are deliberately NOT touched: those are real §767 output.
$OutputFolders = @('Graphics/Sponsors', 'Graphics/SponsorCategories')

function Get-GraphHeaders {
    $s = @{}
    (Get-AzWebApp -ResourceGroupName $RG -Name $FN).SiteConfig.AppSettings | ForEach-Object { $s[$_.Name] = $_.Value }
    $tok = (Invoke-RestMethod -Method Post `
        -Uri "https://login.microsoftonline.com/$($s['SharePoint__TenantId'])/oauth2/v2.0/token" `
        -Body @{
            client_id     = $s['SharePoint__ClientId']
            client_secret = $s['SharePoint__ClientSecret']
            scope         = 'https://graph.microsoft.com/.default'
            grant_type    = 'client_credentials'
        }).access_token
    return @{ Authorization = "Bearer $tok" }
}

function Get-Children($headers, $path) {
    $esc = ($path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    try { (Invoke-RestMethod -Headers $headers -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/root:/${esc}:/children").value }
    catch { @() }
}

$h = Get-GraphHeaders
$removed = 0

# ---- 1. the INPUT ------------------------------------------------------------------
Write-Host "`n== Logo-SoMeBranding (the hand-placed inputs) ==" -ForegroundColor Cyan
$present = Get-Children $h $LogoFolder
foreach ($name in $TestLogos) {
    $item = $present | Where-Object name -eq $name
    if (-not $item) { Write-Host "   already gone: $name" -ForegroundColor DarkGray; continue }
    if ($WhatIf) { Write-Host "   WHATIF delete $name" -ForegroundColor Yellow; continue }
    Invoke-RestMethod -Headers $h -Method Delete -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/items/$($item.id)" | Out-Null
    Write-Host "   deleted $name" -ForegroundColor Green
    $removed++
}

# ⚠️ Anything ELSE in that folder is NOT ours — a sponsor may have used the real upload form since.
$leftovers = (Get-Children $h $LogoFolder) | Where-Object { $TestLogos -notcontains $_.name }
if ($leftovers) {
    Write-Host "   ⚠️ LEFT ALONE (not placed by this test — likely a real sponsor upload):" -ForegroundColor Yellow
    $leftovers | ForEach-Object { Write-Host "      $($_.name)" -ForegroundColor Yellow }
}

# ---- 2. the OUTPUT -----------------------------------------------------------------
foreach ($folder in $OutputFolders) {
    Write-Host "`n== $folder (generated artwork) ==" -ForegroundColor Cyan
    foreach ($item in (Get-Children $h $folder)) {
        if ($WhatIf) { Write-Host "   WHATIF delete $($item.name)" -ForegroundColor Yellow; continue }
        Invoke-RestMethod -Headers $h -Method Delete -Uri "https://graph.microsoft.com/v1.0/drives/$DRIVE/items/$($item.id)" | Out-Null
        Write-Host "   deleted $($item.name)" -ForegroundColor Green
        $removed++
    }
}

# ---- 3. the RECORD -----------------------------------------------------------------
# Sponsor + SponsorCategory rows that the ENGINE wrote (InputHash IS NOT NULL). A null hash would
# mean a pulled/human row, which this test never created and must not delete.
if (-not $SkipDatabase) {
    Write-Host "`n== GraphicAssets rows ==" -ForegroundColor Cyan
    $sql = @"
DELETE FROM GraphicAssets
WHERE Type IN (1, 5)          -- Sponsor, SponsorCategory
  AND InputHash IS NOT NULL;  -- engine-rendered only; never a pulled or overruled row
"@
    if ($WhatIf) {
        $sql = "SELECT COUNT(*) AS WouldDelete FROM GraphicAssets WHERE Type IN (1,5) AND InputHash IS NOT NULL;"
    }
    $token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    Import-Module SqlServer -ErrorAction Stop
    $res = Invoke-Sqlcmd -ServerInstance $SQLSERVER -Database $SQLDB -AccessToken $token -Query $sql
    if ($WhatIf) { Write-Host "   WHATIF would delete $($res.WouldDelete) row(s)" -ForegroundColor Yellow }
    else { Write-Host "   rows deleted" -ForegroundColor Green }
}

Write-Host "`nDone. $removed file(s) removed." -ForegroundColor Cyan
Write-Host "The next sweep will report 0 sponsor graphics — correctly, with nothing to compose." -ForegroundColor DarkGray
