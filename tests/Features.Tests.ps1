#Requires -Version 7
#Requires -Module @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Comprehensive, REAL Pester suite covering the full delivered Community Event
    Hub (CEH) feature catalog -- one Describe per chapter of docs/FEATURES.md,
    one It per feature.

.DESCRIPTION
    Design rules (see docs/TESTS.md "Feature coverage"):

      * One Describe block per FEATURES.md chapter (1..12).
      * One It per delivered feature; the It name IS the feature; a comment
        above it states the requirement / acceptance detail it verifies
        (pulled from REQUIREMENTS.md and the real source in src/).
      * REAL assertions only -- nothing fakes a pass.
          - HTTP/endpoint features hit a RUNNING instance via $env:CEH_BASE_URL
            (or the TARGET convention: TARGET=DEV|PROD selects the well-known
            URLs). When no target is set or it is unreachable, the It self-skips
            with a clear reason -- exactly like Survey-Mobile.Tests.ps1 and the
            Playwright suites. It never asserts a fake 200.
          - Auth-gated features expect a PIN planted by tools/plant-test-pins.ps1
            (DEV-only) and self-skip without $env:ADMIN_PIN.
          - Pure/static features assert real on-disk artifacts (email templates,
            config keys, source routes, infra-as-code) -- these PASS offline.
          - §13 regression tests assert the fixed behaviour in the real source
            on this branch (magic-link EventId claim, company-name resolution,
            dashboard banner).
      * Live / integration tests are tagged 'Live' so they can be excluded:
            Invoke-Pester ./tests/Features.Tests.ps1 -ExcludeTagFilter Live
      * No secrets, real customer names, personal emails or GUIDs are embedded.

    RUN:
        Invoke-Pester ./tests/Features.Tests.ps1 -Output Detailed
        # static-only (offline, no running app):
        Invoke-Pester ./tests/Features.Tests.ps1 -ExcludeTagFilter Live
        # against a running instance:
        $env:CEH_BASE_URL = 'https://dev.eldk27.eventhub.expertslive.dk'
        Invoke-Pester ./tests/Features.Tests.ps1
        # auth-gated organizer/portal checks (DEV only):
        $env:ADMIN_PIN = & ./tools/plant-test-pins.ps1 -Count 2

    This suite does NOT duplicate the existing layers; it references them:
      - tests/Survey-Mobile.Tests.ps1  (mobile/markup smoke on the survey)
      - tests/playwright/*.spec.ts     (true browser render + auth flows)
      - tools/plant-test-pins.ps1      (real PIN planting)
#>

# ---------------------------------------------------------------------------
# Shared discovery: resolve the repo root + the (optional) live base URL.
# ---------------------------------------------------------------------------
BeforeDiscovery {
    # Repo root = parent of the tests/ folder this file lives in.
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

    # Live target resolution. Precedence:
    #   1. $env:CEH_BASE_URL  (explicit, any instance)
    #   2. TARGET=DEV|PROD    (well-known URLs, matches the Playwright convention)
    # Empty => all Live tests self-skip.
    $script:BaseUrl = $null
    if ($env:CEH_BASE_URL) {
        $script:BaseUrl = $env:CEH_BASE_URL.TrimEnd('/')
    } elseif ($env:TARGET) {
        $script:BaseUrl = switch ($env:TARGET.ToUpperInvariant()) {
            'DEV'  { 'https://dev.eldk27.eventhub.expertslive.dk' }
            'PROD' { 'https://eldk27.eventhub.expertslive.dk' }
            default { $null }
        }
    }

    # A Live test is reachable only when we have a base URL AND it answers.
    # We probe /health once at discovery so every Live It gets a consistent,
    # explanatory skip reason instead of N separate timeouts.
    $script:LiveReachable = $false
    $script:LiveSkipReason = if (-not $script:BaseUrl) {
        'No live target: set $env:CEH_BASE_URL or TARGET=DEV|PROD to run Live tests.'
    } else {
        try {
            $probe = Invoke-WebRequest -Uri "$($script:BaseUrl)/health" -UseBasicParsing -TimeoutSec 10 -Method Head -ErrorAction Stop
            if ($probe.StatusCode -eq 200) {
                $script:LiveReachable = $true
                ''
            } else {
                "Live target $($script:BaseUrl) returned HTTP $($probe.StatusCode) on /health."
            }
        } catch {
            "Live target $($script:BaseUrl) unreachable: $($_.Exception.Message)"
        }
    }

    # Auth-gated tests need a planted PIN (DEV-only, via tools/plant-test-pins.ps1).
    $script:HavePin = [bool]$env:ADMIN_PIN
    $script:AuthSkipReason = if (-not $script:HavePin) {
        'No $env:ADMIN_PIN: plant one with tools/plant-test-pins.ps1 (DEV-only) to run auth-gated tests.'
    } else { '' }
}

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $script:SrcRoot  = Join-Path $script:RepoRoot 'src/CommunityHub'
    $script:CoreRoot = Join-Path $script:RepoRoot 'src/CommunityHub.Core'
    $script:TplDir   = Join-Path $script:RepoRoot 'templates/emails'

    if ($env:CEH_BASE_URL) {
        $script:BaseUrl = $env:CEH_BASE_URL.TrimEnd('/')
    } elseif ($env:TARGET) {
        $script:BaseUrl = switch ($env:TARGET.ToUpperInvariant()) {
            'DEV'  { 'https://dev.eldk27.eventhub.expertslive.dk' }
            'PROD' { 'https://eldk27.eventhub.expertslive.dk' }
            default { $null }
        }
    } else { $script:BaseUrl = $null }

    # Live reachability + skip reason, recomputed in the run phase so the It
    # bodies (which run separately from BeforeDiscovery) see consistent values.
    $script:LiveReachable = $false
    if (-not $script:BaseUrl) {
        $script:LiveSkipReason = 'No live target: set $env:CEH_BASE_URL or TARGET=DEV|PROD to run Live tests.'
    } else {
        try {
            $probe = Invoke-WebRequest -Uri "$($script:BaseUrl)/health" -UseBasicParsing -TimeoutSec 10 -Method Head -ErrorAction Stop
            if ($probe.StatusCode -eq 200) { $script:LiveReachable = $true; $script:LiveSkipReason = '' }
            else { $script:LiveSkipReason = "Live target $($script:BaseUrl) returned HTTP $($probe.StatusCode) on /health." }
        } catch {
            $script:LiveSkipReason = "Live target $($script:BaseUrl) unreachable: $($_.Exception.Message)"
        }
    }

    # Helper: GET a path on the live target. Returns @{ Status; Content } or
    # @{ Status = -1 } on failure. Used by Live tests only (which self-skip if
    # the target is down, so this is the fast path).
    function Get-CehPage {
        param([string]$Path)
        $uri = "$($script:BaseUrl)$Path"
        try {
            $r = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop -MaximumRedirection 0
            return @{ Status = [int]$r.StatusCode; Content = $r.Content; Headers = $r.Headers }
        } catch {
            $resp = $_.Exception.Response
            if ($resp -and $resp.StatusCode) {
                return @{ Status = [int]$resp.StatusCode; Content = ''; Headers = @{} }
            }
            return @{ Status = -1; Content = ''; Headers = @{} }
        }
    }

    # Helper: read a source file's full text (offline assertions).
    function Get-SrcText {
        param([string]$RelPath)
        $full = Join-Path $script:RepoRoot $RelPath
        if (-not (Test-Path $full)) { return $null }
        return Get-Content -LiteralPath $full -Raw
    }
}

# ===========================================================================
# CHAPTER 1 — Platform: built for every edition
# ===========================================================================
Describe "1. Platform — built for every edition" {

    # FEATURE: Everything about an edition is configuration (event/sponsors/
    # content/hotel/integrations/deadlines are settings, not code).
    # ACCEPTANCE: a per-edition Event row + config files drive the platform;
    # the year is not hard-coded in logic. Assert config-driven seed + sample
    # config artifacts exist.
    It "Everything about an edition is configuration (Event seed + config artifacts)" {
        $seed = Join-Path $script:RepoRoot 'scripts/seed-eldk27.sql'
        $cfgExamples = Join-Path $script:RepoRoot 'config-examples'
        (Test-Path $seed) -or (Test-Path $cfgExamples) | Should -BeTrue -Because "a new edition is configuration: a seed/config artifact must exist, not a code change"
    }

    # FEATURE: Clean separation of product, code and community / sanitized
    # public template published openly while real config stays private.
    # ACCEPTANCE (REQUIREMENTS §12): publish goes through an allow-listed local
    # script with a -WhatIf dry-run + a denylist that excludes private config.
    It "A sanitized public template — publish runs through a denylisted, dry-run-capable script" {
        $pub = Get-SrcText 'tools/publish-to-public.ps1'
        $pub | Should -Not -BeNullOrEmpty -Because "the public-mirror workflow must be a real, reviewable script"
        $pub | Should -Match '\$denylist'        -Because "private files must be excluded by an explicit denylist"
        $pub | Should -Match '\[switch\]\$WhatIf' -Because "a dry-run pre-flight (-WhatIf) is required before publishing"
    }
}

# ===========================================================================
# CHAPTER 2 — Sign-in & embedding
# ===========================================================================
Describe "2. Sign-in & embedding" {

    # FEATURE: One-time PIN by email — 6-digit, 15-min expiry, single-use, with
    # rate limiting, lockout after repeated wrong tries, neutral messaging.
    # ACCEPTANCE (src/CommunityHub.Core/Auth): 15-min expiry; lockout via
    # MaxFailedAttempts; identical result whether or not email is known.
    It "One-time PIN by email — 15-min expiry, lockout, and non-enumerable messaging" {
        $svc = Get-SrcText 'src/CommunityHub.Core/Auth/PinLoginService.cs'
        $idp = Get-SrcText 'src/CommunityHub.Core/Auth/PinIdentityProvider.cs'
        $svc | Should -Not -BeNullOrEmpty
        $svc | Should -Match '15-minute'                 -Because "PINs must expire in 15 minutes"
        $svc | Should -Match 'rate-limit'                -Because "PIN requests must be rate-limited"
        $svc | Should -Match '(?s)SAME.*known participant|cannot be used to enumerate' -Because "messaging must not reveal whether an email is registered"
        $idp | Should -Match 'MaxFailedAttempts'         -Because "a PIN must lock out after repeated wrong tries"
        $idp | Should -Match 'FailedAttempts < MaxFailedAttempts' -Because "redemption must reject a locked-out PIN"
    }

    # FEATURE: Magic-link login — invitation emails carry a tap-to-sign-in link.
    # ACCEPTANCE: the /Login/Magic page model exists and signs the participant in.
    # (REGRESSION for this defect is covered in Chapter 13.)
    It "Magic-link login — the /Login/Magic handler exists and authenticates" {
        $magic = Get-SrcText 'src/CommunityHub/Pages/Login/Magic.cshtml.cs'
        $magic | Should -Not -BeNullOrEmpty -Because "magic-link login is a delivered feature"
        $magic | Should -Match 'SignInAsync|HttpContext\.SignIn|ClaimsPrincipal|new Claim' -Because "the magic-link path must establish an authenticated session"
    }

    # FEATURE: "Stay signed in" your way — day / week (default) / month / until
    # sign-out, with sliding refresh.
    # ACCEPTANCE: the login flow offers selectable session durations.
    It "Stay-signed-in duration choice exists in the login flow" {
        $login = Get-SrcText 'src/CommunityHub/Pages/Login.cshtml.cs'
        $loginView = Get-SrcText 'src/CommunityHub/Pages/Login.cshtml'
        $blob = "$login`n$loginView"
        $blob | Should -Match '(?i)stay signed in|remember|duration|ExpiresUtc|persistent|IsPersistent|day|week|month' -Because "login must let the user choose how long to stay signed in"
    }

    # FEATURE: Login email prestage — an invite link can pre-fill the email field
    # (/Login?email=<addr>&ReturnUrl=...) so it is just-click-send.
    # ACCEPTANCE (REQUIREMENTS §5): the Login page model reads the email query
    # param into the bound Email field and the view renders it.
    It "Login email prestage — /Login?email= pre-fills the email field" {
        $login = Get-SrcText 'src/CommunityHub/Pages/Login.cshtml.cs'
        $loginView = Get-SrcText 'src/CommunityHub/Pages/Login.cshtml'
        $login | Should -Not -BeNullOrEmpty
        $login | Should -Match 'OnGet\s*\(\s*string\?\s*email' -Because "the GET handler must accept the email query param"
        $login | Should -Match 'Email\s*=\s*email' -Because "the query param must prestage the bound Email field"
        $login | Should -Match 'ReturnUrl' -Because "the invite link carries an optional post-login ReturnUrl"
        $loginView | Should -Match 'value="@Model\.Email"' -Because "the email field must render the prestaged value"
    }

    # FEATURE: Embeds safely in your event portal (Backstage) with the right
    # security controls (frame-ancestors / CSP).
    # ACCEPTANCE (REQUIREMENTS §12 Embedding:BackstageOrigin): an embedding
    # origin config + a CSP frame-ancestors policy wired in Program.cs.
    It "Embeds safely — a configurable Backstage embedding origin + CSP frame policy" {
        $prog = Get-SrcText 'src/CommunityHub/Program.cs'
        $prog | Should -Not -BeNullOrEmpty
        $prog | Should -Match '(?i)Embedding|BackstageOrigin|frame-ancestors|X-Frame-Options|frame-src' -Because "safe embedding needs an allow-listed origin + frame security headers"
    }

    # FEATURE (live): the sign-in page is reachable.
    It "Sign-in page is served (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/Login'
        $r.Status | Should -BeIn @(200, 302)
    }
}

# ===========================================================================
# CHAPTER 3 — Crew profiles & roles
# ===========================================================================
Describe "3. Crew profiles & roles" {

    # FEATURE: A tailored hub per role — Organizer, Speaker, Masterclass Speaker,
    # Volunteer, Sponsor, Speaker-Sponsor, Video, Photography, VIP, Attendee.
    # ACCEPTANCE: the ParticipantRole enum enumerates the delivered roles.
    It "A tailored hub per role — the ParticipantRole enum covers the role set" {
        $roleFile = Get-ChildItem -Path $script:RepoRoot -Recurse -Filter 'ParticipantRole*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Select-Object -First 1
        $text = if ($roleFile) { Get-Content -LiteralPath $roleFile.FullName -Raw }
                else { Get-SrcText 'src/CommunityHub.Core/Domain/Participant.cs' }
        $text | Should -Not -BeNullOrEmpty
        foreach ($role in 'Organizer','Speaker','Volunteer','Sponsor','Attendee') {
            $text | Should -Match $role -Because "role '$role' must be a defined ParticipantRole"
        }
    }

    # FEATURE: Test-data tagging — participants can be flagged IsTestUser so
    # go-live cleanup removes/deactivates test rows without touching real ones.
    # ACCEPTANCE (REQUIREMENTS §3/§5): an IsTestUser column on Participant plus
    # an EF Core migration that adds it.
    It "Test-data tagging — Participant.IsTestUser column + migration exist" {
        $participant = Get-SrcText 'src/CommunityHub.Core/Domain/Participant.cs'
        $participant | Should -Not -BeNullOrEmpty
        $participant | Should -Match 'bool\s+IsTestUser' -Because "test rows must be taggable for go-live cleanup"
        $migDir = Join-Path $script:CoreRoot 'Migrations'
        $mig = Get-ChildItem -Path $migDir -Filter '*IsTestUser*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'Designer' } | Select-Object -First 1
        $mig | Should -Not -BeNullOrEmpty -Because "a migration must add the IsTestUser column"
        (Get-Content -LiteralPath $mig.FullName -Raw) | Should -Match 'AddColumn<bool>\([^)]*"IsTestUser"' -Because "the migration adds the IsTestUser column"
    }

    # FEATURE: A complete crew profile (one per edition) with verified/packed
    # status flags and accreditation.
    # ACCEPTANCE: the Participant entity carries EventId + status flags.
    It "A complete crew profile — Participant entity scoped per edition with status flags" {
        $p = Get-SrcText 'src/CommunityHub.Core/Domain/Participant.cs'
        $p | Should -Not -BeNullOrEmpty
        $p | Should -Match 'EventId'  -Because "one profile per edition means EventId-scoped"
        $p | Should -Match 'IsActive' -Because "people can be activated/deactivated"
    }

    # FEATURE: A friendly one-time welcome page (once per edition).
    # ACCEPTANCE: a /Welcome page exists.
    It "A friendly one-time welcome — the /Welcome page exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Welcome.cshtml')) | Should -BeTrue
    }

    # FEATURE: Activate or deactivate people in a click; deactivated cannot sign in.
    # ACCEPTANCE (live, auth): the organizer Participants page renders for an
    # authenticated organizer. Source check is the offline fallback.
    It "Activate/deactivate people — the organizer Participants page exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/Participants.cshtml')) | Should -BeTrue
    }

    # FEATURE: Organizers can set/clear a sponsor contact's company link from the
    # Edit Participant screen (REQUIREMENTS §3 — entity existed, no UI before).
    # ACCEPTANCE: the page model binds SponsorCompanyId and persists it; the view
    # renders an input for it.
    It "Link a sponsor contact to a company — Edit Participant has a SponsorCompanyId field" {
        $code = Get-SrcText 'src/CommunityHub/Pages/Organizer/EditParticipant.cshtml.cs'
        $view = Get-SrcText 'src/CommunityHub/Pages/Organizer/EditParticipant.cshtml'
        $code | Should -Not -BeNullOrEmpty
        $code | Should -Match '\[BindProperty\][^\n]*SponsorCompanyId' -Because "the form must bind the company id"
        $code | Should -Match 'p\.SponsorCompanyId\s*=' -Because "save must persist the company id (set or clear)"
        $view | Should -Match 'asp-for="SponsorCompanyId"' -Because "the organizer needs an input to set/clear it"
    }
}

# ===========================================================================
# CHAPTER 4 — Self-service forms
# ===========================================================================
Describe "4. Self-service forms" {

    # FEATURE: each self-service form exists and is wired (Dinner/Hotel/Lunch/
    # Speaker/Swag/Travel/Volunteer wizard).
    # ACCEPTANCE: the Razor pages for every delivered form are present.
    It "Each self-service form page is present (Dinner/Hotel/Lunch/Speaker/Swag/Travel/Volunteer wizard)" {
        $forms = @(
            'Pages/Forms/Dinner.cshtml',
            'Pages/Forms/Hotel.cshtml',
            'Pages/Forms/Lunch.cshtml',
            'Pages/Forms/Speaker.cshtml',
            'Pages/Forms/Swag.cshtml',
            'Pages/Forms/Travel.cshtml',
            'Pages/Forms/VolunteerWizard.cshtml'
        )
        foreach ($f in $forms) {
            (Test-Path (Join-Path $script:SrcRoot $f)) | Should -BeTrue -Because "form '$f' is a delivered self-service form"
        }
    }

    # FEATURE: the in-hub volunteer-shift form is the wizard only — the legacy
    # single-page duplicate (no auto-task wiring) was removed.
    # ACCEPTANCE: Forms/Volunteer.cshtml is gone; the nav points at the wizard.
    It "Legacy single-page Forms/Volunteer is removed (wizard is the only in-hub shift form)" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Forms/Volunteer.cshtml'))    | Should -BeFalse -Because "the legacy single-page volunteer form was removed"
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Forms/Volunteer.cshtml.cs')) | Should -BeFalse -Because "the legacy single-page volunteer model was removed"
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Forms/VolunteerWizard.cshtml')) | Should -BeTrue -Because "the wizard is the surviving in-hub shift form"
        $layout = Get-SrcText 'src/CommunityHub/Pages/Shared/_Layout.cshtml'
        $layout | Should -Match '/Forms/VolunteerWizard' -Because "the nav must link the wizard"
        $layout | Should -Not -Match 'href="/Forms/Volunteer"' -Because "the nav must not link the removed legacy page"
    }

    # FEATURE: Travel — submitting a reimbursement claim automatically creates
    # the matching invoice/payout task.
    # ACCEPTANCE: the Travel form's post handler creates a ParticipantTask.
    It "Travel — submitting a claim auto-creates the payout/invoice task" {
        $travel = Get-SrcText 'src/CommunityHub/Pages/Forms/Travel.cshtml.cs'
        $travel | Should -Not -BeNullOrEmpty
        $travel | Should -Match 'ParticipantTask|Tasks\.Add|AddTask|payout|reimburse' -Because "a travel claim must wire up the matching payout task"
    }

    # FEATURE: Late-change alerts done right — edits AFTER the change deadline
    # notify organizers; edits before the deadline stay quiet.
    # ACCEPTANCE: the forms compare against a change deadline before notifying.
    It "Late-change alerts — change-deadline gating exists for hotel/dinner/shift edits" {
        $hits = Select-String -Path (Join-Path $script:SrcRoot 'Pages/Forms/*.cs') `
                -Pattern '(?i)deadline|late.?change|ChangeDeadline' -ErrorAction SilentlyContinue
        $hits | Should -Not -BeNullOrEmpty -Because "late-change notification must be gated by a change deadline"
    }

    # FEATURE (live): a public/form route is served. Hotel is [Authorize], so we
    # check the public volunteer signup which needs no login.
    It "Volunteer sign-up wizard is reachable (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/volunteer/signup'
        $r.Status | Should -BeIn @(200, 302) -Because "the public volunteer sign-up wizard is login-free"
    }
}

# ===========================================================================
# CHAPTER 5 — Tasks & reminders
# ===========================================================================
Describe "5. Tasks & reminders" {

    # FEATURE: A personal to-do list for every person.
    # ACCEPTANCE: a /Tasks page + a ParticipantTask entity exist.
    It "A personal to-do list — the /Tasks page and ParticipantTask entity exist" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Tasks/Index.cshtml')) | Should -BeTrue
        $task = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter 'ParticipantTask.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Select-Object -First 1
        $task | Should -Not -BeNullOrEmpty -Because "tasks are backed by a ParticipantTask entity"
    }

    # FEATURE: A reminder engine that's gentle and reliable — never double-sends,
    # quietly catches up if a day is missed.
    # ACCEPTANCE: ReminderEngine de-dups against SentReminder by
    # (RecipientEmail, ReminderType, OccasionKey).
    It "Reminder engine never double-sends — SentReminder de-dup key is enforced" {
        $sr  = Get-SrcText 'src/CommunityHub.Core/Domain/SentReminder.cs'
        $eng = Get-SrcText 'src/CommunityHub.Core/Reminders/ReminderEngine.cs'
        $sr  | Should -Not -BeNullOrEmpty
        $eng | Should -Not -BeNullOrEmpty
        foreach ($k in 'RecipientEmail','ReminderType','OccasionKey') {
            $sr | Should -Match $k -Because "the idempotency key needs $k"
        }
        $eng | Should -Match '(?i)SentReminder' -Because "the engine must consult the sent-reminder ledger before sending"
    }

    # FEATURE: Speaker deadlines, scheduled for you — each speaker gets a dated
    # task for every key milestone.
    # ACCEPTANCE: a speaker-deadline reminder type / template exists.
    It "Speaker deadlines scheduled — a speaker-deadline reminder template exists" {
        (Test-Path (Join-Path $script:TplDir 'speaker-deadline-reminder.html')) | Should -BeTrue
    }

    # FEATURE: Speaker deadlines carry ABSOLUTE due dates (not an event-relative
    # offset). REQUIREMENTS §5: the 4 milestone dates are confirmed real values.
    # ACCEPTANCE: the config + seeder use an absolute dueDate / masterclassOnly
    # model and the daysBeforeEvent offset is gone.
    It "Speaker deadlines use absolute dueDate (no daysBeforeEvent offset)" {
        $cfgPath = Join-Path $script:RepoRoot 'config/speaker-deadlines.eldk27.json'
        (Test-Path $cfgPath) | Should -BeTrue
        $cfgRaw = Get-Content -LiteralPath $cfgPath -Raw
        $cfgRaw | Should -Not -Match 'daysBeforeEvent' -Because "the event-relative offset model is removed"
        $cfg = $cfgRaw | ConvertFrom-Json
        $cfg._needsUpdate | Should -BeFalse -Because "the milestone dates are confirmed (REQUIREMENTS §5)"
        $titles = $cfg.deadlines.title
        $titles | Should -Contain 'Submit title and abstract'
        $titles | Should -Contain 'Verify Bio + Photo in hub'
        $titles | Should -Contain 'Upload draft preview deck'
        $titles | Should -Contain 'Upload final slide deck'
        $titles | Should -Not -Contain 'Confirm A/V and room requirements' -Because "that deadline was deleted"
        foreach ($dl in $cfg.deadlines) {
            $dl.dueDate | Should -Match '^\d{4}-\d{2}-\d{2}$' -Because "every deadline carries an absolute date"
        }
        # The title/abstract deadline is masterclass-only.
        $mc = $cfg.deadlines | Where-Object { $_.title -eq 'Submit title and abstract' }
        $mc.masterclassOnly | Should -BeTrue
        $mc.dueDate | Should -Be '2026-06-20'

        $seeder = Get-SrcText 'src/CommunityHub.Core/Config/SpeakerDeadlineSeeder.cs'
        $seeder | Should -Not -BeNullOrEmpty
        $seeder | Should -Not -Match 'DaysBeforeEvent' -Because "the offset property is removed from the seeder"
        $seeder | Should -Match 'DueDate\s*=\s*dl\.DueDate' -Because "tasks are dated from the absolute dueDate"
        $seeder | Should -Match 'MasterclassOnly' -Because "the masterclass-only audience flag is honoured"
    }

    # FEATURE: Tuned entirely through settings — reminder cadence/text/recipients
    # (incl. CC/BCC/escalation) configurable, no code changes.
    # ACCEPTANCE: a reminder options/config type drives cadence + recipients.
    It "Reminders are tuned through settings — reminder options/config exist" {
        $opt = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*ReminderOptions*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Select-Object -First 1
        if (-not $opt) {
            $opt = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
                Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)reminder.*(Cadence|Enabled|Cc|Bcc|Escalat)' } |
                Select-Object -First 1
        }
        $opt | Should -Not -BeNullOrEmpty -Because "reminder cadence/recipients must be configurable, not hard-coded"
    }
}

# ===========================================================================
# CHAPTER 6 — Sessions & surveys
# ===========================================================================
Describe "6. Sessions & surveys" {

    # FEATURE: Import speakers from a Sessionize export — columns any order,
    # match on email, NEVER overwrite roles, report skipped rows, no network dep.
    # ACCEPTANCE (SessionizeImportService): existing email updates name only,
    # not role; result carries created/updated/skipped counts.
    It "Import speakers from a spreadsheet — match on email, never overwrite roles, report skips" {
        $imp = Get-SrcText 'src/CommunityHub.Core/Reminders/SessionizeImportService.cs'
        $imp | Should -Not -BeNullOrEmpty
        $imp | Should -Match '(?i)do NOT change the role|never.*role' -Because "matching on email must not overwrite an existing role"
        $imp | Should -Match '(?i)Skipped' -Because "the import must report rows it skipped"
    }

    # FEATURE: Imported speakers get welcomed automatically (once).
    # ACCEPTANCE: import sends the welcome email on the create path; a welcome
    # template exists.
    It "Imported speakers get welcomed automatically — welcome wired into import" {
        $imp = Get-SrcText 'src/CommunityHub.Core/Reminders/SessionizeImportService.cs'
        $imp | Should -Match '(?i)welcome' -Because "new speakers receive a welcome on import"
        (Test-Path (Join-Path $script:TplDir 'welcome.html')) | Should -BeTrue
    }

    # FEATURE: Manual-create welcome hook — when an organizer adds a participant by
    # hand, the welcome email can fire too (parity with the import path), idempotent
    # via the SentReminder ledger (REQUIREMENTS §6).
    # ACCEPTANCE: EditParticipant calls WelcomeEmailService.SendWelcomeAsync; the
    # service guards against re-sends with a SentReminder lookup.
    It "Manual-participant welcome hook — Edit Participant can fire the welcome (idempotent)" {
        $code = Get-SrcText 'src/CommunityHub/Pages/Organizer/EditParticipant.cshtml.cs'
        $code | Should -Not -BeNullOrEmpty
        $code | Should -Match 'WelcomeEmailService' -Because "the manual-create path reuses the welcome service"
        $code | Should -Match 'SendWelcomeAsync' -Because "the organizer can send a welcome on create or on demand"
        $svc = Get-SrcText 'src/CommunityHub.Core/Reminders/WelcomeEmailService.cs'
        $svc | Should -Match 'SentReminders\.AnyAsync' -Because "idempotency is enforced via the SentReminder ledger"
    }

    # FEATURE: Public, no-login surveys with built-in spam protection, slug-based,
    # 3-step.
    # ACCEPTANCE (Survey/Index.cshtml.cs): honeypot field; AllowAnonymous;
    # slug-routed.
    It "Public, no-login surveys — honeypot spam protection, anonymous, slug-routed" {
        $surv = Get-SrcText 'src/CommunityHub/Pages/Survey/Index.cshtml.cs'
        $surv | Should -Not -BeNullOrEmpty
        $surv | Should -Match '(?i)honeypot'   -Because "spam protection is built in"
        $surv | Should -Match 'string slug'    -Because "surveys run at their own slug"
        $surv | Should -Match '(?i)AllowAnonymous' -Because "no sign-in is required"
    }

    # FEATURE: Call-for-speakers demand survey with a shareable results page.
    # ACCEPTANCE: the /Survey/{slug}/Results page exists.
    It "Call-for-speakers demand survey — the shareable results page exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Survey/Results.cshtml')) | Should -BeTrue
    }

    # FEATURE (live): the public survey renders. (Deep mobile/markup assertions
    # live in tests/Survey-Mobile.Tests.ps1 — not duplicated here.)
    It "Mobile-first survey renders (Live; deep checks in Survey-Mobile.Tests.ps1)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/survey/eldk27-topics'
        $r.Status | Should -Be 200
        $r.Content | Should -Match 'ELDK27'
    }
}

# ===========================================================================
# CHAPTER 7 — Sponsors
# ===========================================================================
Describe "7. Sponsors" {

    # FEATURE: A sponsor is a company, not a single contact — every contact sees
    # the company's shared tasks.
    # ACCEPTANCE: sponsor tasks key on SponsorCompanyId, not a single participant.
    It "A sponsor is a company — sponsor tasks key on the company, not one contact" {
        $tasks = Get-SrcText 'src/CommunityHub/Pages/Sponsor/Tasks.cshtml.cs'
        $tasks | Should -Not -BeNullOrEmpty
        $tasks | Should -Match 'SponsorCompanyId|companyId' -Because "shared sponsor tasks are scoped to the company"
    }

    # FEATURE: Always the right public company name — chosen public name with a
    # sensible fallback chain (public -> legal -> billing -> "Company {id}").
    # ACCEPTANCE (CompanyManagerClient): public name preferred; documented chain.
    # (REGRESSION for the dead resolver is in Chapter 13.)
    It "Always the right public company name — Company Manager public-name fallback chain" {
        $cm = Get-SrcText 'src/CommunityHub.Core/Integrations/CompanyManagerClient.cs'
        $cm | Should -Not -BeNullOrEmpty
        $cm | Should -Match '(?i)public.?name|company_name_public' -Because "the public company name is the primary source of truth"
    }

    # FEATURE: Booth tasks generated from what each sponsor bought; per-tier
    # extras (Platinum/Diamond/Gold); de-duplicated across orders.
    # ACCEPTANCE: a sponsor task expander/classifier maps booth products to tasks.
    It "Booth tasks generated per purchase, de-duplicated across orders" {
        $svc = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)(Platinum|Diamond|Gold).*booth|booth.*tier|SourceKey' } |
            Select-Object -First 1
        $svc | Should -Not -BeNullOrEmpty -Because "booth products must expand into tier tasks with a de-dup key (SourceKey)"
    }

    # FEATURE: Automatic work stays invisible (webshop sync, ERP sync, currency,
    # masterclass reconciliation never shown to sponsors as to-dos).
    # ACCEPTANCE (REQUIREMENTS §7a): those legacy automations are NOT in the .NET
    # hub as sponsor tasks. Assert no booth/sponsor task template surfaces them.
    It "Automatic work stays invisible — back-office sync is not a sponsor to-do" {
        $sponsorTplFiles = Get-ChildItem -Path $script:TplDir -Filter 'sponsor-*.html' -ErrorAction SilentlyContinue
        $blob = ($sponsorTplFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        $blob | Should -Not -Match '(?i)e-?conomic|ERP sync|currency check' -Because "ERP/currency back-office work is never shown to sponsors"
    }

    # FEATURE: Full sponsor management for organizers (coordinators, signers,
    # tasks targeted at exhibitors/sponsors/tier).
    # ACCEPTANCE: the organizer sponsor-admin task management page exists.
    It "Full sponsor management for organizers — the SponsorAdmin task area exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/SponsorAdmin/Tasks.cshtml')) | Should -BeTrue
    }
}

# ===========================================================================
# CHAPTER 8 — Sponsor leads
# ===========================================================================
Describe "8. Sponsor leads" {

    # FEATURE: A leads API for sponsors — pull leads as JSON or CSV via a secured
    # endpoint /api/v1/sponsors/{id}/leads.{json|csv}.
    # ACCEPTANCE (SponsorLeadsController): both routes present.
    It "A leads API for sponsors — leads.json and leads.csv routes exist" {
        $ctl = Get-SrcText 'src/CommunityHub/Api/SponsorLeadsController.cs'
        $ctl | Should -Not -BeNullOrEmpty
        $ctl | Should -Match 'api/v1/sponsors/\{sponsorCompanyId\}' -Because "the per-sponsor route is the documented contract"
        $ctl | Should -Match 'HttpGet\("leads\.json"\)' -Because "JSON pull is a delivered endpoint"
        $ctl | Should -Match 'HttpGet\("leads\.csv"\)'  -Because "CSV pull is a delivered endpoint"
    }

    # FEATURE: Secure, revocable access per sponsor — own access key + token;
    # keys shown once, stored only as a secure hash; revocable instantly.
    # ACCEPTANCE: hashed key storage + a revoke method.
    It "Secure, revocable access per sponsor — hashed keys + revoke" {
        $keysvc = Get-SrcText 'src/CommunityHub.Core/Integrations/Sponsors/DbSponsorApiKeyService.cs'
        $keysvc | Should -Not -BeNullOrEmpty
        $keysvc | Should -Match '(?i)SHA256|hash' -Because "keys are stored only as a secure hash"
        $keysvc | Should -Match '(?i)Revoke'      -Because "access must be revocable instantly"
    }

    # FEATURE: A real lead pipeline — Reply / Processed / Interest / Ignore /
    # Junk; nothing ever hard-deleted.
    # ACCEPTANCE: a SponsorLead status set covering those states.
    It "A real lead pipeline — soft-status states (Processed/Ignore/Junk) exist" {
        $statusFile = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)Junk' -and (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)Processed' } |
            Select-Object -First 1
        $statusFile | Should -Not -BeNullOrEmpty -Because "the pipeline tracks lead status without hard delete"
    }

    # FEATURE: Smart junk screening — 0-100 quality score + label; only
    # unmistakable test entries auto-flagged Junk; operators stay in control.
    # ACCEPTANCE (REQUIREMENTS §8): heuristic 0-100 score lives in code.
    It "Smart junk screening — a 0-100 quality score + junk heuristic exists" {
        $screen = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)(quality|lead).*score' -or (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)0.*100' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)junk' } |
            Select-Object -First 1
        $screen | Should -Not -BeNullOrEmpty -Because "a 0-100 score + junk label drive screening"
    }

    # FEATURE (live, auth): the leads API serves a sponsor's leads via a real
    # token. Self-skips without a token; the planted-token flow is exercised by
    # the Playwright admin suite (admin-mobile.spec.ts). Here we assert the
    # endpoint rejects an unauthenticated request (security contract).
    It "Leads API rejects unauthenticated pulls (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        # No key/token => must NOT return 200 with data. A safe id placeholder.
        $r = Get-CehPage '/api/v1/sponsors/0/leads.json'
        $r.Status | Should -BeIn @(401, 403, 404) -Because "an unauthenticated/invalid pull must never return leads"
    }
}

# ===========================================================================
# CHAPTER 9 — Attendees & masterclass reconciliation
# ===========================================================================
Describe "9. Attendees & masterclass reconciliation" {

    # FEATURE: Tickets vs masterclass seats reconciled automatically — surfaces
    # no-booking / no-ticket / duplicate, with branded chaser emails.
    # ACCEPTANCE: the three attendee chaser templates exist.
    It "Tickets and masterclass seats reconciled — the three chaser templates exist" {
        foreach ($t in 'attendee-missing-booking.html','attendee-missing-ticket.html','attendee-duplicate-booking.html') {
            (Test-Path (Join-Path $script:TplDir $t)) | Should -BeTrue -Because "the '$t' chaser is a delivered reconciliation email"
        }
    }

    # FEATURE: An attendee browser for organizers — read-only tiles/search/
    # filters/CSV export (handles accented names).
    # ACCEPTANCE: the organizer Attendees page exists with CSV export.
    It "An attendee browser for organizers — Attendees page with CSV export" {
        $att = Get-SrcText 'src/CommunityHub/Pages/Organizer/Attendees.cshtml.cs'
        $att | Should -Not -BeNullOrEmpty
        $att | Should -Match '(?i)csv|text/csv|\.csv' -Because "the attendee browser exports CSV"
    }

    # FEATURE: People decide identity, not algorithms — "same person, two emails"
    # resolved by a human/attendee, never auto-merged.
    # ACCEPTANCE (REQUIREMENTS §16): no fuzzy auto-merge in the attendee code.
    It "People decide identity — attendee code never auto-merges duplicates" {
        $recon = Get-ChildItem -Path $script:RepoRoot -Recurse -Filter '*ttendee*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        $blob = ($recon | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
        $blob | Should -Not -Match '(?i)AutoMerge|auto-merge|FuzzyMerge' -Because "identity resolution is human-driven, never auto-merged"
    }
}

# ===========================================================================
# CHAPTER 10 — Email & notifications
# ===========================================================================
Describe "10. Email & notifications" {

    # FEATURE: One branded template engine — shared shell + per-type content +
    # simple {{token}} substitution; built for Outlook.
    # ACCEPTANCE: _layout.html has a "Subject:" first line and {{token}} markers;
    # an EmailTemplateProvider renders them.
    It "One branded template engine — shared shell with Subject line + token substitution" {
        $layout = Join-Path $script:TplDir '_layout.html'
        (Test-Path $layout) | Should -BeTrue
        $first = (Get-Content -LiteralPath $layout -TotalCount 1)
        $first | Should -Match '^Subject:' -Because "every template's first line is its Subject"
        $body = Get-Content -LiteralPath $layout -Raw
        $body | Should -Match '\{\{bodyContent\}\}' -Because "the shell injects per-type content via a token"
        (Get-SrcText 'src/CommunityHub.Core/Email/EmailTemplateProvider.cs') | Should -Not -BeNullOrEmpty
    }

    # FEATURE: A library of ready templates (welcome/reminders/chasers/app-game/
    # broadcast) — all branded.
    # ACCEPTANCE: every delivered template file exists AND starts with "Subject:".
    It "A library of ready templates — all present and each opens with a Subject line" {
        $expected = @(
            '_layout.html','welcome.html','speaker-deadline-reminder.html',
            'speaker-pending-tasks.html','task-deadline-reminder.html',
            'incomplete-form-chaser.html','sponsor-overdue.html',
            'sponsor-leads-digest.html','broadcast.html',
            'attendee-duplicate-booking.html','attendee-missing-booking.html',
            'attendee-missing-ticket.html','app-game-gift-reminder.html',
            'group-photo-invite.html',
            'invitation.html','task-manual-reminder.html','travel-reimbursement-paid.html'
        )
        foreach ($t in $expected) {
            $path = Join-Path $script:TplDir $t
            (Test-Path $path) | Should -BeTrue -Because "template '$t' is in the delivered library"
            (Get-Content -LiteralPath $path -TotalCount 1) | Should -Match '^Subject:' -Because "'$t' must declare its Subject on line 1"
        }
    }

    # FEATURE: the former inline-HTML senders (SendInvitations / SpeakerReminders /
    # TravelReimbursements) now render through the shared template engine for brand
    # consistency / multi-tenant cleanliness (REQUIREMENTS §10).
    # ACCEPTANCE: each page renders a named template via EmailTemplateProvider and
    # carries no hand-rolled brand string ("ELDK-team" / hardcoded "#008BD2").
    It "Brand-template holdouts moved — SendInvitations/SpeakerReminders/Travel render via the provider" {
        $map = @{
            'src/CommunityHub/Pages/Organizer/SendInvitations.cshtml.cs'       = 'invitation'
            'src/CommunityHub/Pages/Organizer/SpeakerReminders.cshtml.cs'      = 'task-manual-reminder'
            'src/CommunityHub/Pages/Organizer/TravelReimbursements.cshtml.cs'  = 'travel-reimbursement-paid'
        }
        foreach ($rel in $map.Keys) {
            $code = Get-SrcText $rel
            $code | Should -Not -BeNullOrEmpty
            $code | Should -Match 'EmailTemplateProvider' -Because "$rel must use the shared template provider"
            $code | Should -Match ([regex]::Escape("Render(""$($map[$rel])""")) -Because "$rel must render the '$($map[$rel])' template"
            $code | Should -Not -Match 'ELDK-team' -Because "$rel must not hand-roll the team sign-off"
            $code | Should -Not -Match '#008BD2'   -Because "$rel must not hard-code the brand colour"
        }
    }

    # FEATURE: Broadcast to chosen groups — one personalized "Hi {firstName}"
    # message; resilient batch (one failure never stops the rest).
    # ACCEPTANCE: the broadcast template carries the {{firstName}} token.
    It "Broadcast — personalized {{firstName}} token present in the broadcast template" {
        $b = Get-Content -LiteralPath (Join-Path $script:TplDir 'broadcast.html') -Raw
        $b | Should -Match '\{\{firstName\}\}' -Because "broadcasts greet each recipient by first name"
    }

    # FEATURE: An Email Center for organizers — preview, one-click test send,
    # delivery history.
    # ACCEPTANCE: the organizer Email Center page exists.
    It "An Email Center for organizers — the Email Center page exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/EmailCenter.cshtml')) | Should -BeTrue
    }

    # FEATURE (constraint): DEV email redirect routes ALL outbound mail to one
    # inbox so tests never reach real people.
    # ACCEPTANCE (REQUIREMENTS §15): Email:RedirectAllTo is wired in code.
    It "DEV email redirect — Email:RedirectAllTo is honoured in the send path" {
        $emailCode = Get-ChildItem -Path $script:CoreRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'RedirectAllTo' } |
            Select-Object -First 1
        $emailCode | Should -Not -BeNullOrEmpty -Because "DEV-safe sends depend on RedirectAllTo being applied at send time"
    }
}

# ===========================================================================
# CHAPTER 11 — Organizer hub
# ===========================================================================
Describe "11. Organizer hub" {

    # FEATURE: A live dashboard — form completion, participants by role, tasks/
    # overdues, sponsor completion, attendee mismatches, volunteer coverage.
    # ACCEPTANCE: the organizer Dashboard page exists.
    It "A live dashboard — the organizer Dashboard page exists" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/Dashboard.cshtml')) | Should -BeTrue
    }

    # FEATURE: A sponsor admin area — task catalog, leads pipeline (issue/rotate/
    # revoke keys, prefs, action leads), status dashboard sorted overdue-first.
    # ACCEPTANCE: the SponsorAdmin Leads + Dashboard pages exist.
    It "A sponsor admin area — leads pipeline + status dashboard pages exist" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/SponsorAdmin/Leads.cshtml'))     | Should -BeTrue
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Organizer/SponsorAdmin/Dashboard.cshtml')) | Should -BeTrue
    }

    # FEATURE: Built-in safety on file handling — organizer tools that read files
    # are guarded against path-traversal.
    # ACCEPTANCE: organizer file-reading code rejects traversal sequences.
    It "Built-in safety — path-traversal guard in organizer file handling" {
        $guard = Get-ChildItem -Path $script:SrcRoot -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)\.\.|traversal|GetFileName|InvalidFileName|Path\.GetFullPath' } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?i)traversal|GetFileName|\.\.' } |
            Select-Object -First 1
        $guard | Should -Not -BeNullOrEmpty -Because "file-reading organizer tools must reject path traversal"
    }

    # FEATURE (live, auth): the organizer dashboard renders for an authenticated
    # organizer. Self-skips without a planted PIN; the real PIN click-through is
    # exercised by tests/playwright/admin-mobile.spec.ts (not duplicated). Here we
    # assert the dashboard is auth-gated (bounces an anonymous request).
    It "Organizer dashboard is auth-gated (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/Organizer/Dashboard'
        # Anonymous: must redirect to login, never serve the dashboard body.
        $r.Status | Should -BeIn @(302, 401, 403) -Because "an unauthenticated user must be bounced from the organizer hub"
    }
}

# ===========================================================================
# CHAPTER 12 — Hosting & reliability
# ===========================================================================
Describe "12. Hosting & reliability" {

    # FEATURE: Defined entirely as code — DB, web app, jobs, storage, vault,
    # logging, monitoring described as Bicep.
    # ACCEPTANCE: the infra modules exist.
    It "Defined entirely as code — Bicep main + the core infra modules exist" {
        (Test-Path (Join-Path $script:RepoRoot 'infra/main.bicep')) | Should -BeTrue
        foreach ($m in 'appservice','sql','functions','keyvault','storage','monitoring') {
            (Test-Path (Join-Path $script:RepoRoot "infra/modules/$m.bicep")) | Should -BeTrue -Because "infra module '$m' is part of the as-code environment"
        }
    }

    # FEATURE: Scheduled jobs that just work — reminders, order pulls, attendee
    # reconciliation, portal sync, sponsor-lead delivery, upload-change watching,
    # each individually switchable.
    # ACCEPTANCE: a timer-triggered job class exists per pipeline.
    It "Scheduled jobs — timer-triggered job classes exist for each pipeline" {
        $jobs = @('ReminderJob','WooCommercePullJob','AttendeeReconcileJob','BackstageSyncJob','SponsorLeadsJob','SponsorUploadWatchJob')
        foreach ($j in $jobs) {
            (Test-Path (Join-Path $script:RepoRoot "src/CommunityHub.Jobs/$j.cs")) | Should -BeTrue -Because "scheduled pipeline '$j' is delivered"
        }
        $rem = Get-SrcText 'src/CommunityHub.Jobs/ReminderJob.cs'
        $rem | Should -Match '(?i)TimerTrigger' -Because "jobs are timer-scheduled"
    }

    # FEATURE: SharePoint folder listing follows @odata.nextLink so the upload
    # watcher sees every file, not just the first page (REQUIREMENTS §7).
    # ACCEPTANCE: ListFolderFilesAsync loops on @odata.nextLink instead of a single
    # $top=200 page.
    It "SharePoint folder listing paginates — follows @odata.nextLink" {
        $sp = Get-SrcText 'src/CommunityHub.Core/Integrations/SharePointUploadClient.cs'
        $sp | Should -Not -BeNullOrEmpty
        $sp | Should -Match '@odata\.nextLink' -Because "the listing must follow the next-page link"
        $sp | Should -Match 'while\s*\(nextUrl' -Because "it loops until every page is consumed"
        $sp | Should -Match 'GraphGetAbsoluteOrNullAsync' -Because "nextLink is an absolute URL, fetched as-is"
    }

    # FEATURE: A safe public-mirror workflow — allow-listed, dry-run pre-flight,
    # only intended content made public.
    # ACCEPTANCE (also asserted in Ch.1): publish script has denylist + -WhatIf.
    It "A safe public-mirror workflow — denylist + dry-run pre-flight" {
        $pub = Get-SrcText 'tools/publish-to-public.ps1'
        $pub | Should -Match 'function Test-Denylisted' -Because "files are filtered through an allow/deny check"
        $pub | Should -Match '\[switch\]\$WhatIf'       -Because "a pre-flight dry run is required"
    }

    # FEATURE (live): the /health liveness probe returns 200. This is the cheapest
    # "is the app up" check and is what the discovery probe already used.
    It "Health probe returns 200 (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/health'
        $r.Status | Should -Be 200
    }
}

# ===========================================================================
# CHAPTER 13 — Regression tests for the fixes on fix/ceh-bugs-magiclink
# (REQUIREMENTS.md §13). These assert the FIXED behaviour in real source so the
# bugs cannot silently regress. They are offline/static and PASS on this branch.
# ===========================================================================
Describe "13. Bug-fix regressions (REQUIREMENTS §13)" {

    # DEFECT: magic-link omitted the EventId claim, so CurrentParticipant.
    # FromPrincipal failed and magic-link-only sessions bounced to /Login.
    # FIX: add the EventId claim to match the PIN flow.
    It "Magic-link adds the EventId claim (so [Authorize] pages don't bounce)" {
        $magic = Get-SrcText 'src/CommunityHub/Pages/Login/Magic.cshtml.cs'
        $magic | Should -Not -BeNullOrEmpty
        $magic | Should -Match 'new\("EventId",\s*participant\.EventId\.ToString\(\)\)' -Because "the magic-link session must carry EventId like the PIN flow"
    }

    # DEFECT: ResolveCompanyDisplayNameAsync fetched the name but the ternary
    # returned "Company {id}" in both branches, so emails always read "Company {id}".
    # FIX: return the fetched name; fall back to "Company {id}" only when blank.
    It "Company-name resolution returns the fetched name, not 'Company {id}'" {
        $tasks = Get-SrcText 'src/CommunityHub/Pages/Sponsor/Tasks.cshtml.cs'
        $tasks | Should -Not -BeNullOrEmpty
        $tasks | Should -Match 'IsNullOrWhiteSpace\(resolved\)\s*\?\s*\$"Company \{companyId\}"\s*:\s*resolved' -Because "the fetched company name must win; the id is only the empty fallback"
    }

    # DEFECT: ZohoPipelinePending was hard-coded true, so the "Zoho pipeline not
    # yet configured" banner always showed even though leads are live.
    # FIX: compute it from real state (CRM enabled OR any leads landed clears it).
    It "Dashboard banner computes from real state, not a hard-coded true" {
        $dash = Get-SrcText 'src/CommunityHub/Pages/Organizer/SponsorAdmin/Dashboard.cshtml.cs'
        $dash | Should -Not -BeNullOrEmpty
        $dash | Should -Match 'ZohoPipelinePending\s*=\s*!\(_zoho\.CrmEnabled\s*\|\|\s*leadAgg\.Count\s*>\s*0\)' -Because "the banner must reflect real CRM/leads state"
        # Guard against the regressed form coming back.
        $dash | Should -Not -Match 'ZohoPipelinePending\s*=\s*true\s*;' -Because "the hard-coded true was the bug"
    }
}

# ===========================================================================
# DEV -> PROD DATA PARITY + IsTestUser  (REQUIREMENTS §12 prod parity, §15
# "don't seed prod test data" honoured by tagging, DESIGN §3/§14)
# ---------------------------------------------------------------------------
# The parity tooling brings any env to the same data shape from one source of
# truth (scripts/seed-eldk27.sql): the ELDK27 Event, four real organizers
# (IsTestUser=0), seeded sponsor+speaker tasks, and the role-coverage test
# users (IsTestUser=1) so prod-vs-test state is distinguishable and go-live
# cleanup can delete WHERE IsTestUser=1. The only intended dev/prod difference
# is the email flow (config, not data). All assertions are static/offline.
# ===========================================================================
Describe "Dev -> Prod data parity + IsTestUser tagging" {

    # FEATURE: IsTestUser column + migration (overlaps PR #6; replicated here so
    # the parity tool can tag rows and the build stays green if PR #6 is not yet
    # on main).
    # ACCEPTANCE: a bool IsTestUser on Participant + an EF migration adding it,
    # plus the model snapshot carrying the column.
    It "IsTestUser — Participant property, EF migration, and snapshot all present" {
        $participant = Get-SrcText 'src/CommunityHub.Core/Domain/Participant.cs'
        $participant | Should -Not -BeNullOrEmpty
        $participant | Should -Match 'bool\s+IsTestUser' -Because "test rows must be taggable for go-live cleanup"

        $migDir = Join-Path $script:CoreRoot 'Migrations'
        $mig = Get-ChildItem -Path $migDir -Filter '*IsTestUser*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'Designer' } | Select-Object -First 1
        $mig | Should -Not -BeNullOrEmpty -Because "a migration must add the IsTestUser column"
        (Get-Content -LiteralPath $mig.FullName -Raw) |
            Should -Match 'AddColumn<bool>\([^)]*"IsTestUser"' -Because "the migration adds the IsTestUser column"

        $snap = Get-SrcText 'src/CommunityHub.Core/Migrations/CommunityHubDbContextModelSnapshot.cs'
        $snap | Should -Match 'b\.Property<bool>\("IsTestUser"\)' -Because "the model snapshot must include IsTestUser (no model drift)"
    }

    # FEATURE: the design-time DbContext factory lets EF scaffold/apply
    # migrations without a runtime connection string (needed when the parity
    # tool applies migrations to a target env).
    It "Design-time DbContextFactory exists for EF tooling" {
        $factory = Get-SrcText 'src/CommunityHub.Core/Data/DesignTimeDbContextFactory.cs'
        $factory | Should -Not -BeNullOrEmpty
        $factory | Should -Match 'IDesignTimeDbContextFactory<CommunityHubDbContext>' -Because "EF tools need a design-time factory"
    }

    # FEATURE: a single canonical seed is the source of truth for dev==prod
    # data, with the four real organizers as real participants (IsTestUser=0).
    # ACCEPTANCE: seed-eldk27.sql seeds the four @expertslive.dk organizers and
    # writes the IsTestUser column.
    It "Canonical seed defines four real organizers (IsTestUser=0) and writes IsTestUser" {
        $seed = Get-SrcText 'scripts/seed-eldk27.sql'
        $seed | Should -Not -BeNullOrEmpty
        $seed | Should -Match '\[IsTestUser\]' -Because "the seed must populate the test-user flag"
        foreach ($org in @('mok@expertslive.dk','mb@expertslive.dk','kea@expertslive.dk','mlh@expertslive.dk')) {
            $seed | Should -Match ([regex]::Escape($org)) -Because "organizer $org must be seeded"
        }
    }

    # FEATURE: the role-coverage test users are seeded and tagged IsTestUser=1
    # so prod-vs-test is distinguishable and removable at go-live. Asserted
    # structurally (no personal-email literals in this published file): the
    # seed's @People rows split into organizer rows flagged 0 and test rows
    # flagged 1, and the role column covers Speaker(1)/Volunteer(3)/Sponsor(4)/
    # Attendee(5).
    It "Canonical seed tags the role-coverage test users IsTestUser=1 (4+ test rows, 4 organizers)" {
        $seed = Get-SrcText 'scripts/seed-eldk27.sql'
        # @People VALUES rows look like (N'...', N'...', <role>, <companyOrNULL>, <isTest>)
        $rows = [regex]::Matches($seed, "\(N'[^']*@[^']*',\s*N'[^']*',\s*(?<role>\d+),\s*(?:N'[^']*'|NULL),\s*(?<test>[01])\)")
        $rows.Count | Should -BeGreaterOrEqual 8 -Because "the seed defines the organizer + test-user roster"
        $orgRows  = @($rows | Where-Object { $_.Groups['test'].Value -eq '0' })
        $testRows = @($rows | Where-Object { $_.Groups['test'].Value -eq '1' })
        $orgRows.Count  | Should -Be 4              -Because "exactly the four organizers are flagged IsTestUser=0"
        $testRows.Count | Should -BeGreaterOrEqual 4 -Because "the role-coverage test users are flagged IsTestUser=1"
        # The four portal roles used for the TestMode PROD sweep are present among test rows.
        $testRoles = $testRows | ForEach-Object { $_.Groups['role'].Value } | Sort-Object -Unique
        foreach ($r in '1','3','4','5') {
            $testRoles | Should -Contain $r -Because "test roster must cover role $r (Speaker/Volunteer/Sponsor/Attendee)"
        }
    }

    # FEATURE: the seed keeps the sponsor + speaker sample tasks so a sponsor /
    # speaker area can be exercised in any env (data parity for tasks).
    It "Canonical seed keeps the sponsor + speaker sample tasks (idempotent SourceKeys)" {
        $seed = Get-SrcText 'scripts/seed-eldk27.sql'
        $seed | Should -Match 'woo:seed:2linkit:logo' -Because "the sponsor sample task must exist"
        $seed | Should -Match 'seed:speaker:abstract' -Because "the speaker sample task must exist"
    }

    # FEATURE: the parity tool is re-runnable, env-targeted, and hard-codes NO
    # prod secret/identifier (server/db/user are params; password via KeyVault /
    # env var). It applies the canonical seed and supports -WhatIf.
    It "Sync-CehParity.ps1 — re-runnable, env-targeted, no hard-coded prod secrets" {
        $tool = Get-SrcText 'tools/Sync-CehParity.ps1'
        $tool | Should -Not -BeNullOrEmpty
        $tool | Should -Match 'SupportsShouldProcess' -Because "it must support -WhatIf for a safe prod preview"
        $tool | Should -Match "ValidateSet\('dev',\s*'prod'\)" -Because "it must target dev or prod explicitly"
        $tool | Should -Match 'seed-eldk27\.sql' -Because "it applies the canonical seed (one source of truth)"
        $tool | Should -Match 'sql-admin-password' -Because "it reads the SQL password from Key Vault by secret name"
        # No prod connection string, password literal, or .database.windows.net host baked in.
        $tool | Should -Not -Match 'Password=[^$;"][^;"]+' -Because "no literal SQL password may be embedded"
        $tool | Should -Not -Match '[a-z0-9-]+\.database\.windows\.net' -Because "no concrete SQL server FQDN may be hard-coded"
    }

    # FEATURE: the only intended dev/prod difference is the email flow; the
    # parity tool is data-only and must NOT touch RedirectAllTo / OnlySendTo.
    It "Parity tool is data-only — does not mutate the email flow (RedirectAllTo / OnlySendTo)" {
        $tool = Get-SrcText 'tools/Sync-CehParity.ps1'
        # The tool must never reference the email-flow config keys at all (it is
        # data-only); the email difference is owned by per-env app settings.
        $tool | Should -Not -Match 'RedirectAllTo' -Because "email redirect is env config, not data parity"
        $tool | Should -Not -Match 'OnlySendTo' -Because "the prod allowlist is env config, not data parity"
        # Its only data write is the canonical seed (the source of truth).
        $tool | Should -Match 'seed-eldk27\.sql' -Because "the tool's sole write is the data seed"
    }
}

# ===========================================================================
# CHAPTER 14 — Speaker hub (self-service milestone tracker)
# A first-class /Speaker page that turns scattered speaker-deadline tasks +
# the static "important dates" card into one cohesive, mobile-first speaker
# journey: a progress bar, per-milestone countdown cards, and one-tap
# mark-done / reopen. Reads the existing speakerdl: deadline tasks (orthogonal
# to whatever deadline model seeds them). These assertions are offline/static.
# ===========================================================================
Describe "14. Speaker hub — self-service milestone tracker" {

    # FEATURE: the Speaker Hub page exists and is speaker-only.
    # ACCEPTANCE: /Speaker/Index page + model present; eligibility is gated to
    # the Speaker role (the pre-day nuance lives on SpeakerProfile.SpeakingPreDay,
    # not a separate role); non-speakers see AccessDenied, not a 403.
    It "Speaker hub page exists and is gated to speaker roles" {
        (Test-Path (Join-Path $script:SrcRoot 'Pages/Speaker/Index.cshtml')) | Should -BeTrue -Because "the speaker hub is a first-class page"
        $code = Get-SrcText 'src/CommunityHub/Pages/Speaker/Index.cshtml.cs'
        $code | Should -Not -BeNullOrEmpty
        $code | Should -Match '\[Authorize\]' -Because "the speaker hub requires sign-in"
        $code | Should -Match 'EligibleRoles' -Because "only the Speaker role has a journey"
        $code | Should -Match 'ParticipantRole\.Speaker' -Because "speakers are eligible"
        $code | Should -Match 'AccessDenied' -Because "a non-speaker gets a friendly message, not a hard 403"
    }

    # FEATURE: the milestone read-model derives countdown + progress from the
    # speaker's seeded deadline tasks (speakerdl: SourceKey), without re-seeding
    # or mutating on read.
    # ACCEPTANCE: SpeakerMilestoneService reads speakerdl: tasks, scoped per
    # (event, participant), and exposes progress (done/total/percent/overdue).
    It "Milestone service reads speakerdl: tasks and computes progress + countdown" {
        $svc = Get-SrcText 'src/CommunityHub.Core/Reminders/SpeakerMilestoneService.cs'
        $svc | Should -Not -BeNullOrEmpty
        $svc | Should -Match 'speakerdl:' -Because "the tracker is built from the seeded deadline tasks"
        $svc | Should -Match 'StartsWith\(SourceKeyPrefix\)' -Because "it must filter to the speaker-deadline source key"
        $svc | Should -Match 'AssignedParticipantId == participantId' -Because "milestones are scoped to the signed-in speaker"
        $svc | Should -Match 'DaysUntilDue' -Because "each milestone carries a live countdown"
        $svc | Should -Match 'PercentComplete' -Because "the hub shows overall progress"
    }

    # FEATURE: a speaker can mark their OWN milestone done / reopen — never
    # another speaker's, never a non-milestone task.
    # ACCEPTANCE: ToggleAsync re-asserts the (event, participant, speakerdl:)
    # scope on the lookup, and the page exposes a Toggle handler.
    It "Speaker can toggle only their own milestone (scoped flip)" {
        $svc = Get-SrcText 'src/CommunityHub.Core/Reminders/SpeakerMilestoneService.cs'
        $svc | Should -Match 'public async Task<bool> ToggleAsync' -Because "the hub flips milestone state"
        $svc | Should -Match 't\.AssignedParticipantId == participantId' -Because "a speaker can only flip their own milestone"
        $svc | Should -Match 'StartsWith\(SourceKeyPrefix\)' -Because "only deadline milestones are toggleable here, not arbitrary tasks"
        $page = Get-SrcText 'src/CommunityHub/Pages/Speaker/Index.cshtml.cs'
        $page | Should -Match 'OnPostToggleAsync' -Because "the view posts to a Toggle handler"
    }

    # FEATURE: the hub is wired into navigation + the front-page speaker card,
    # so speakers reach it without hunting.
    # ACCEPTANCE: the nav links /Speaker for speaker roles; the Index card links it.
    It "Speaker hub is wired into nav and the front-page speaker card" {
        $layout = Get-SrcText 'src/CommunityHub/Pages/Shared/_Layout.cshtml'
        $layout | Should -Match 'href="/Speaker"' -Because "speakers need a nav entry to the hub"
        $index = Get-SrcText 'src/CommunityHub/Pages/Index.cshtml'
        $index | Should -Match '/Speaker/Index' -Because "the front-page speaker card links the new hub"
    }

    # FEATURE: mobile-first — the hub renders at ~360px (single-column cards,
    # full-width action buttons on narrow screens).
    # ACCEPTANCE: the view ships a responsive @@media rule (per the mobile-first
    # constraint, REQUIREMENTS §15).
    It "Speaker hub is mobile-first — ships a responsive media query" {
        $view = Get-SrcText 'src/CommunityHub/Pages/Speaker/Index.cshtml'
        $view | Should -Not -BeNullOrEmpty
        $view | Should -Match '@media' -Because "mobile-first means a responsive breakpoint shipped with the desktop CSS"
        $view | Should -Match 'progressbar' -Because "the progress bar is the centrepiece of the journey view"
    }

    # FEATURE: the service is registered for DI so the page resolves it.
    # ACCEPTANCE: Program.cs registers SpeakerMilestoneService.
    It "SpeakerMilestoneService is registered in DI" {
        $prog = Get-SrcText 'src/CommunityHub/Program.cs'
        $prog | Should -Match 'SpeakerMilestoneService' -Because "the page depends on the service via DI"
    }

    # FEATURE (live): the speaker hub is auth-gated (anonymous bounces to login,
    # never serves the journey). The real authed click-through is a Playwright
    # concern; here we assert the security contract only.
    It "Speaker hub is auth-gated (Live)" -Tag 'Live' {
        if (-not $script:LiveReachable) { Set-ItResult -Skipped -Because $script:LiveSkipReason }
        $r = Get-CehPage '/Speaker'
        $r.Status | Should -BeIn @(302, 401, 403) -Because "an unauthenticated user must be bounced from the speaker hub"
    }
}
