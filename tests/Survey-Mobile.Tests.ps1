#Requires -Version 7
#Requires -Module @{ ModuleName='Pester'; ModuleVersion='5.0.0' }
<#
.SYNOPSIS
    Smoke test for the /survey/eldk27-topics page against DEV + PROD.

.DESCRIPTION
    Catches the kinds of regressions we've actually hit in this iteration:
      - Hero band visibility (the iPhone-clipped-h1 bug, 2026-06-10)
      - Master -> Black Belt rename leakage (no stale "Master" string)
      - Step photos missing from the publish output
      - Topbar + footer wording present
      - Mobile media query (@media (max-width: 600px)) shipped

    Run before any survey-related deploy:
        pwsh ./tests/Survey-Mobile.Tests.ps1
        # or via Invoke-Pester:
        Invoke-Pester ./tests/Survey-Mobile.Tests.ps1 -Output Detailed

    For true visual / pixel-level mobile testing, install Playwright + add
    a .spec.ts under tests/playwright/. This Pester suite is the fast smoke
    test that runs in seconds and catches the obvious breakage.
#>

BeforeDiscovery {
    $script:Environments = @(
        @{ Name = 'DEV';  BaseUrl = 'https://dev.eldk27.eventhub.expertslive.dk' }
        @{ Name = 'PROD'; BaseUrl = 'https://eldk27.eventhub.expertslive.dk' }
    )
}

Describe "/survey/eldk27-topics renders on <Name>" -ForEach $script:Environments {
    BeforeAll {
        $script:url  = "$BaseUrl/survey/eldk27-topics"
        $script:html = $null
        try {
            $resp = Invoke-WebRequest -Uri $script:url -UseBasicParsing -TimeoutSec 30
            $script:statusCode = $resp.StatusCode
            $script:html       = $resp.Content
        } catch {
            $script:statusCode = -1
            $script:html       = ''
            Write-Warning "Failed to fetch ${script:url}: $($_.Exception.Message)"
        }
    }

    Context "HTTP" {
        It "returns 200 OK" { $script:statusCode | Should -Be 200 }
        It "returns non-empty HTML" { $script:html.Length | Should -BeGreaterThan 1000 }
    }

    Context "Hero band (mobile-safe)" {
        It "renders the new title 'Help shape the topics for ELDK27'" {
            $script:html | Should -Match 'Help shape the topics for ELDK27'
        }
        It "renders the subtitle with the event date" {
            $script:html | Should -Match 'Experts Live Denmark 2027'
            # Dash between 9 and 10 may be ASCII '-', '–' (en-dash), or its HTML
            # entity form '&#x2013;' / '&ndash;' depending on the encoder;
            # allow ANY (short) sequence -- entities can include digits + letters.
            $script:html | Should -Match '9.{1,20}10 February 2027'
        }
        It "uses padding-based hero (not the brittle min-height-only flex centring that clipped iPhone)" {
            # We require BOTH: a survey-hero class AND a mobile media query
            $script:html | Should -Match 'class="survey-hero"'
            $script:html | Should -Match '@media\s*\(\s*max-width:\s*600px\s*\)'
        }
    }

    Context "Step photos (must ship with the publish output)" {
        It "references /img/survey-step1.jpg in step 1" { $script:html | Should -Match '/img/survey-step1\.jpg' }
        It "references /img/survey-step2.jpg in step 2" { $script:html | Should -Match '/img/survey-step2\.jpg' }
        It "references /img/survey-step3.jpg in step 3" { $script:html | Should -Match '/img/survey-step3\.jpg' }
        It "actually serves /img/survey-step1.jpg (200)" {
            (Invoke-WebRequest -Uri "$BaseUrl/img/survey-step1.jpg" -UseBasicParsing -TimeoutSec 15 -Method Head).StatusCode | Should -Be 200
        }
        It "actually serves /img/survey-step2.jpg (200)" {
            (Invoke-WebRequest -Uri "$BaseUrl/img/survey-step2.jpg" -UseBasicParsing -TimeoutSec 15 -Method Head).StatusCode | Should -Be 200
        }
        It "actually serves /img/survey-step3.jpg (200)" {
            (Invoke-WebRequest -Uri "$BaseUrl/img/survey-step3.jpg" -UseBasicParsing -TimeoutSec 15 -Method Head).StatusCode | Should -Be 200
        }
    }

    Context "Brand logos (centered header)" {
        It "renders Experts Live Denmark logo" { $script:html | Should -Match '/img/logo-experts-live-denmark-white\.png' }
        It "renders ELDK27 event logo"        { $script:html | Should -Match '/img/logo-eldk27-event\.png' }
        It "centers the logos in the header"  { $script:html | Should -Match 'justify-content:\s*center' }
    }

    Context "Topbar (event-site + ticket sale)" {
        It "renders the topbar"                       { $script:html | Should -Match 'class="topbar"' }
        It "shows the ticket-sale info"               { $script:html | Should -Match 'Ticket sale starts' }
        It "links to https://eldk27.expertslive.dk/"  { $script:html | Should -Match 'https://eldk27\.expertslive\.dk/' }
        It "has the 'Visit event site' CTA button"    { $script:html | Should -Match 'Visit event site' }
    }

    Context "Footer (consistent across CEH)" {
        It "renders the footer"                     { $script:html | Should -Match '<footer' }
        It "credits CEH + Morten + aka.ms/morten"   { $script:html | Should -Match 'Community Event Hub'; $script:html | Should -Match 'aka\.ms/morten' }
        It "shows the Submit-a-bug link"            { $script:html | Should -Match 'Submit a bug' }
    }

    Context "Master -> Black Belt rename (no stale 'Master' label)" {
        It "uses 'Black Belt' for the third level" {
            $script:html | Should -Match 'Black Belt'
        }
        It "shows the MS-event level numbers (300 / 400 / 500)" {
            # These appear in the wizard Step 3 radios via JS-rendered template;
            # confirm the static JSON-embedded variant string is in the page source.
            $script:html | Should -Match 'Advanced \(300\)'
            $script:html | Should -Match 'Expert \(400\)'
            $script:html | Should -Match 'Black Belt \(500\)'
        }
        It "no stale 'Master' label as a level name" {
            # Allowed: any 'master' substring inside neutral words (e.g. SCCM 'master site').
            # Disallowed: 'Master (500)' or '> Master <' or 'Master ' as a standalone
            # level label. We assert the level-specific patterns are gone.
            $script:html | Should -Not -Match "'Master',\s*3"
            $script:html | Should -Not -Match 'Master \(500\)'
        }
    }

    Context "Results dashboard sister page (/results)" {
        BeforeAll {
            $script:resultsHtml = (Invoke-WebRequest -Uri "$BaseUrl/survey/eldk27-topics/results" `
                                  -UseBasicParsing -TimeoutSec 30).Content
            # Empty-state path renders when there are 0 responses -- the
            # ranking/share/level-totals sections only appear when
            # TotalResponses > 0. Skip those assertions accordingly.
            $script:hasResponses = $script:resultsHtml -notmatch 'No responses yet'
        }
        It "returns the results page successfully" { $script:resultsHtml.Length | Should -BeGreaterThan 1000 }
        It "never shows the old 'X pts' wording" {
            # Old: 'X pts'. New: '#1', '#2', ... + 'X favorites'. Always assertable
            # regardless of response count.
            $script:resultsHtml | Should -Not -Match '\s\d+ pts\b'
        }
        It "ranks topics under the Most-wanted heading" -Skip:(!$script:hasResponses) {
            $script:resultsHtml | Should -Match 'Most-wanted topics overall'
        }
        It "renders the Share button (not the bare URL)" -Skip:(!$script:hasResponses) {
            $script:resultsHtml | Should -Match 'class="share-btn"'
            $script:resultsHtml | Should -Match 'Share this dashboard with prospective speakers'
        }
        It "renders 'Black Belt (level 500)' on the level totals card" -Skip:(!$script:hasResponses) {
            $script:resultsHtml | Should -Match 'Black Belt \(level 500\)'
        }
    }
}

Describe "Cross-environment parity (DEV must match PROD on the survey)" {
    It "DEV + PROD both serve a survey title that contains 'ELDK27'" {
        foreach ($env in $script:Environments) {
            $html = (Invoke-WebRequest -Uri "$($env.BaseUrl)/survey/eldk27-topics" `
                     -UseBasicParsing -TimeoutSec 30).Content
            $html | Should -Match 'ELDK27' -Because "Expected '$($env.Name)' to render the ELDK27 title"
        }
    }
}
