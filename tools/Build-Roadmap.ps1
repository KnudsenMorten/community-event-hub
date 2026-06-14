#Requires -Version 5.1
<#
.SYNOPSIS
    Generate a public, high-level ROADMAP.md from the internal REQUIREMENTS.md backlog.

.DESCRIPTION
    Part of the shared documentation model (see CLAUDE_STRUCTURE_CONTEXT.md).
    REQUIREMENTS.md is the internal backlog (never published). ROADMAP.md is the
    PUBLIC, customer-friendly, high-level overview of *planned / upcoming features*.

    This script derives ROADMAP.md from REQUIREMENTS.md deterministically at publish
    time, so the roadmap is always in sync with the backlog and is never hand-edited.

    Rules applied:
      - Only backlog items (lines marked `◻` or `🟡`) are included.
      - Bug fixes / defects are EXCLUDED (item text matching bug/fix/defect/hotfix,
        and whole chapters titled "Known defects/issues").
      - Internal-only chapters are EXCLUDED (Constraints / Do-Nots / Out of scope /
        Placeholders).
      - Each item is reduced to its high-level title (the **bold** lead text, or the
        text before the first em-dash) — detail and status markers are dropped.
      - Items are grouped under their REQUIREMENTS.md chapter headings.

    The output is intentionally LESS detailed than REQUIREMENTS.md (high-level only).

.PARAMETER RequirementsPath
    Path to the source REQUIREMENTS.md.

.PARAMETER OutputPath
    Path to write the generated ROADMAP.md.

.PARAMETER ProjectName
    Display name used in the ROADMAP title.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RequirementsPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ProjectName = 'Project'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $RequirementsPath)) {
    throw "Build-Roadmap: REQUIREMENTS.md not found at '$RequirementsPath'."
}

# Resolve to absolute paths up front — [System.IO.File] uses the .NET process working
# directory, NOT the PowerShell location, so a relative -OutputPath would otherwise be
# written to the wrong folder when the caller has `cd`'d elsewhere.
$RequirementsPath = (Resolve-Path -LiteralPath $RequirementsPath).Path
if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path (Get-Location).Path $OutputPath
}

$lines = Get-Content -LiteralPath $RequirementsPath

# Whole chapters that are NOT roadmap material (rules / defects / non-features).
$skipChapter = 'defect|constraint|do-?not|out[ -]of[ -]scope|placeholder|known issue|known defect|delivery status|how to use'
# Item text that is a bug fix rather than a planned feature.
$skipItem    = '\bbug\b|\bfix(es|ed|ing)?\b|\bdefect\b|hotfix|regression'

$out = New-Object System.Collections.Generic.List[string]
$out.Add("# $ProjectName — Roadmap")
$out.Add("")
$out.Add("> Planned & upcoming **features**, grouped by area. A high-level overview — for what is")
$out.Add("> already available see the product's feature catalog. Bug fixes are not listed here.")
$out.Add(">")
$out.Add("> **Auto-generated from the internal backlog at publish time — do not hand-edit.**")
$out.Add("")

$curChapter     = $null
$chapterEmitted = $false
$skipCur        = $false
$any            = $false

foreach ($ln in $lines) {
    if ($ln -match '^#{2,}\s+(.+?)\s*$') {
        $title  = $Matches[1].Trim()
        $clean  = ($title -replace '^\d+[a-z]?\.\s*', '').Trim()   # drop "12. " / "7a. "
        $clean  = ($clean -replace '\s*\(.*?\)\s*$', '').Trim()    # drop trailing "(...)" notes
        $skipCur        = ($clean -imatch $skipChapter)
        $curChapter     = $clean
        $chapterEmitted = $false
        continue
    }
    if ($skipCur) { continue }

    if ($ln -match '^\s*[-*]\s+(.*)$') {
        $body = $Matches[1]
        if ($body -notmatch '◻|🟡') { continue }       # backlog items only
        if ($body -imatch $skipItem) { continue }       # not bug fixes

        $item = $null
        if ($body -match '\*\*(.+?)\*\*') {
            $item = $Matches[1]
        } else {
            $t = ($body -replace '◻|🟡|✅', '').Trim()
            $t = ($t -split '\s+[—-]\s+', 2)[0]
            $item = $t.Trim()
        }
        $item = ($item -replace '`', '').Trim()
        if (-not $item) { continue }

        if (-not $chapterEmitted -and $curChapter) {
            if ($any) { $out.Add("") }
            $out.Add("## $curChapter")
            $out.Add("")
            $chapterEmitted = $true
        }
        $out.Add("- $item")
        $any = $true
    }
}

if (-not $any) {
    $out.Add("_No planned features in the backlog right now — everything captured is delivered._")
}
$out.Add("")

$enc = New-Object System.Text.UTF8Encoding($false)
$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutputPath, ($out -join "`n"), $enc)
Write-Host "Build-Roadmap: wrote $OutputPath ($([int]$any) section(s) of planned features) from $RequirementsPath"
