#Requires -Version 5.1
<#
.SYNOPSIS
    One-time governance setup for the eldk-community-event-hub private repo.
    Run this once after the workflows / CONTRIBUTING.md land on main.

.DESCRIPTION
    Uses the GitHub CLI (`gh`) to:
      1. Enable branch protection on `main`:
         - Require a PR before merging
         - Require 1 approving review
         - Require the `pr-validate.yml` status check to pass
         - Require linear history
         - Block direct pushes (including admin)
      2. Add the listed team members as Write collaborators
      3. Verify the PUBLIC_REPO_PAT secret is present (only prints whether it
         exists -- you still have to create the PAT and set it manually because
         the GH CLI can't read secret values back)

.PARAMETER PrivateRepo
    Default: KnudsenMorten/eldk-community-event-hub. The private repo to harden.

.PARAMETER Collaborators
    Array of GitHub usernames to add as Write collaborators. Pass empty array
    if you just want to set branch protection without adding people.

.PARAMETER RequiredReviews
    Number of approving reviews required on a PR before merge. Default 1.
    Bump to 2 if you want stricter co-signing.

.PARAMETER AllowAdminBypass
    Default $false (admins -- including you -- must also go through PR).
    Set $true if you want to keep an emergency-override route.

.EXAMPLE
    pwsh ./tools/setup-repo-governance.ps1 -Collaborators 'alice','bob','charlie'

.EXAMPLE
    pwsh ./tools/setup-repo-governance.ps1 -RequiredReviews 2 -AllowAdminBypass

.NOTES
    Requires gh CLI authenticated with admin scope on the private repo:
        gh auth login -- and pick a scope that includes admin:repo or
                         the "Manage repository settings" fine-grained perm.
#>

[CmdletBinding()]
param(
    [string]$PrivateRepo = 'KnudsenMorten/eldk-community-event-hub',
    [string[]]$Collaborators = @(),
    [int]$RequiredReviews = 1,
    [switch]$AllowAdminBypass
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$m) Write-Host "`n>> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "   $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "   $m" -ForegroundColor Yellow }

# Sanity-check gh CLI is installed + authenticated.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) not found. Install: https://cli.github.com/"
}
$ghStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "gh CLI not authenticated. Run: gh auth login (pick a scope that includes admin permissions)."
}

# -----------------------------------------------------------------------------
# 1. Branch protection on main
# -----------------------------------------------------------------------------
Write-Step "Setting branch protection on $PrivateRepo@main"

$bpBody = @{
    required_status_checks = @{
        strict   = $true
        contexts = @('validate')   # job name in pr-validate.yml
    }
    enforce_admins = (-not $AllowAdminBypass.IsPresent)
    required_pull_request_reviews = @{
        dismiss_stale_reviews             = $true
        require_code_owner_reviews        = $false
        required_approving_review_count   = $RequiredReviews
    }
    restrictions       = $null
    required_linear_history = $true
    allow_force_pushes      = $false
    allow_deletions         = $false
    block_creations         = $false
    required_conversation_resolution = $true
} | ConvertTo-Json -Depth 10 -Compress

# Use the REST API via gh api (PUT) to set branch protection. The shorthand
# `gh repo edit` doesn't cover required reviews + status checks together.
$bpBody | gh api `
    --method PUT `
    "/repos/$PrivateRepo/branches/main/protection" `
    --input - `
    --header 'Accept: application/vnd.github+json' | Out-Null

Write-Ok "Branch protection applied (PR required, $RequiredReviews approval(s), pr-validate must pass, no force-push, linear history)."
if ($AllowAdminBypass) {
    Write-Warn "Admins can bypass (you asked for -AllowAdminBypass)."
} else {
    Write-Ok "Admins CANNOT bypass -- everyone goes through PR."
}

# -----------------------------------------------------------------------------
# 2. Invite collaborators
# -----------------------------------------------------------------------------
if ($Collaborators.Count -gt 0) {
    Write-Step "Inviting $($Collaborators.Count) collaborator(s) with Write access"
    foreach ($user in $Collaborators) {
        try {
            $body = @{ permission = 'push' } | ConvertTo-Json -Compress
            $body | gh api --method PUT "/repos/$PrivateRepo/collaborators/$user" --input - | Out-Null
            Write-Ok "Invited: $user"
        } catch {
            Write-Warn "Could not invite $user -- $($_.Exception.Message)"
        }
    }
    Write-Host ""
    Write-Host "  Collaborators will get an email -- they must accept before they can push." -ForegroundColor DarkGray
} else {
    Write-Step "No collaborators passed -- skipping invites"
    Write-Host "   (To invite later: re-run with -Collaborators 'alice','bob')" -ForegroundColor DarkGray
}

# -----------------------------------------------------------------------------
# 3. Check the PUBLIC_REPO_PAT secret exists
# -----------------------------------------------------------------------------
Write-Step "Checking PUBLIC_REPO_PAT secret"
$secrets = gh secret list --repo $PrivateRepo 2>$null
if ($secrets -match '^PUBLIC_REPO_PAT\b') {
    Write-Ok "PUBLIC_REPO_PAT exists -- publish-public.yml can authenticate to push to the public repo."
} else {
    Write-Warn "PUBLIC_REPO_PAT is NOT set."
    Write-Host ""
    Write-Host "   Create a fine-grained PAT at: https://github.com/settings/personal-access-tokens/new" -ForegroundColor Yellow
    Write-Host "     - Repository access  : Only select repositories -> KnudsenMorten/community-event-hub" -ForegroundColor Yellow
    Write-Host "     - Repository perms   : Contents = Read and write" -ForegroundColor Yellow
    Write-Host "     - Expiry             : 90d or 1y (your call -- shorter is safer)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "   Then store it:" -ForegroundColor Yellow
    Write-Host "     gh secret set PUBLIC_REPO_PAT --repo $PrivateRepo --body '<paste-PAT-here>'" -ForegroundColor Yellow
}

# -----------------------------------------------------------------------------
# Summary
# -----------------------------------------------------------------------------
Write-Step "Done"
Write-Host "  Branch protection : main is locked down" -ForegroundColor Green
Write-Host "  Collaborators     : $($Collaborators.Count) invited" -ForegroundColor Green
Write-Host "  PAT for publish   : $((Get-Variable -Name 'secrets' -ValueOnly) -match '^PUBLIC_REPO_PAT\b' ? 'set' : 'MISSING -- see notes above')" -ForegroundColor Green
Write-Host ""
Write-Host "  Next: have your team accept their invites, then they can clone + branch + PR." -ForegroundColor Cyan
Write-Host "  Trigger first publish: git tag public-v0.1.0 && git push origin public-v0.1.0" -ForegroundColor Cyan
