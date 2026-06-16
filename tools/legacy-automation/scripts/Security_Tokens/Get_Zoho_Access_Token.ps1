# Zoho EU OAuth: Get Authorization Code -> Exchange for Access & Refresh Token
# Minimal, single-run script (no menus)
# - Replace the config values below
# - Run the script, approve in browser, paste the redirect URL or code when prompted

# =========================
# CONFIG
# =========================
# Zoho OAuth client id/secret are read from Key Vault by name (zoho-client-id /
# zoho-client-secret) — never hard-code them here. Set $env:ELDK_KEYVAULT_NAME
# (operator config, not a secret) to the event Key Vault.
Import-Module "$PSScriptRoot\..\Secrets.psm1" -Force
$vault        = $env:ELDK_KEYVAULT_NAME
if ([string]::IsNullOrWhiteSpace($vault)) { throw "Set `$env:ELDK_KEYVAULT_NAME to your Key Vault name." }
$ClientId     = Get-KvSecretValue -VaultName $vault -SecretName 'zoho-client-id'
$ClientSecret = Get-KvSecretValue -VaultName $vault -SecretName 'zoho-client-secret'   # leave empty in KV for SelfClient
$RedirectUri  = "https://expertslive.dk"
$Scopes       = @(
  "zohobackstage.exhibitor.CREATE",
  "zohobackstage.exhibitor.UPDATE",
  "zohobackstage.exhibitor.READ",
  "zohobackstage.exhibitor.DELETE",
  "zohobackstage.sponsor.READ",
  "zohobackstage.speaker.CREATE",
  "zohobackstage.speaker.READ",
  "zohobackstage.attendee.READ",
  "zohobackstage.eventticket.READ",
  "zohobackstage.order.READ",
  "zohobackstage.event.READ",
  "zohobackstage.portal.READ",
  "zohobookings.data.READ",
  "zohobookings.data.CREATE"
)

# Force consent helps ensure Zoho issues a NEW refresh token
$ForceConsent = $true

# =========================
# CONSTANTS (EU DC)
# =========================
$AuthBase  = "https://accounts.zoho.eu/oauth/v2"
$TokenUrl  = "$AuthBase/token"

# =========================
# HELPERS
# =========================
function UrlEncode([string]$v) {
  try { [System.Net.WebUtility]::UrlEncode($v) }
  catch { Add-Type -AssemblyName System.Web; [System.Web.HttpUtility]::UrlEncode($v) }
}

# =========================
# 1) Build & open Auth URL
# =========================
$scopeStr = ($Scopes -join ",")
$qs = @(
  "response_type=code",
  "client_id=$(UrlEncode $ClientId)",
  "scope=$(UrlEncode $scopeStr)",
  "redirect_uri=$(UrlEncode $RedirectUri)",
  "access_type=offline",
  "state=xyz"
)
if ($ForceConsent) { $qs += "prompt=consent" }
$AuthUrl = "$AuthBase/auth?$($qs -join '&')"

Write-Host "Open this URL to authorize:" -ForegroundColor Cyan
Write-Host $AuthUrl
try { Start-Process $AuthUrl | Out-Null } catch {}

# =========================
# 2) Paste redirect URL or code
# =========================
$inputVal = Read-Host "`nPaste the full redirect URL (or just the code value)"
$authCode = $null

if ($inputVal -match "code=") {
  # Try to parse code from URL
  try {
    $uri = [System.Uri]$inputVal
    $q = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
    $authCode = $q["code"]
  } catch {
    # Fallback: naive parse
    if ($inputVal -match "code=([^&]+)") { $authCode = $matches[1] }
  }
} else {
  $authCode = $inputVal.Trim()
}

if ([string]::IsNullOrWhiteSpace($authCode)) {
  throw "No authorization code found. Make sure you pasted the full redirect URL or the exact code."
}

# =========================
# 3) Exchange code -> tokens
# =========================
$body = @{
  grant_type   = "authorization_code"
  client_id    = $ClientId
  redirect_uri = $RedirectUri
  code         = $authCode
}
if ($ClientSecret -and $ClientSecret.Trim()) { $body.client_secret = $ClientSecret }

try {
  $resp = Invoke-RestMethod -Method Post -Uri $TokenUrl `
    -ContentType "application/x-www-form-urlencoded" -Body $body -ErrorAction Stop
} catch {
  Write-Host "Token exchange failed:" -ForegroundColor Red
  throw
}

# =========================
# 4) Output tokens
# =========================
Write-Host "`n=== TOKENS ===" -ForegroundColor Yellow
if ($resp.api_domain)   { Write-Host ("api_domain   : {0}" -f $resp.api_domain) }
if ($resp.token_type)   { Write-Host ("token_type   : {0}" -f $resp.token_type) }
if ($resp.expires_in)   { Write-Host ("expires_in   : {0}" -f $resp.expires_in) }
if ($resp.access_token) { Write-Host ("access_token : {0}" -f $resp.access_token) }
if ($resp.refresh_token){
  Write-Host ("refresh_token: {0}" -f $resp.refresh_token) -ForegroundColor Green
} else {
  Write-Host "No refresh_token returned. If you previously authorized this client, re-run with ForceConsent = `$true, or revoke the existing grant and try again." -ForegroundColor Red
}

# Optional: write raw JSON to stdout
# $resp | ConvertTo-Json -Depth 10
