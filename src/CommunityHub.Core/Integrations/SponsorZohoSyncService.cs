using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Pushes a sponsor company's Company-Details fields to Zoho Backstage on
/// "Save &amp; Sync to Zoho". Every paying company is a Zoho SPONSOR; companies
/// that bought booth products are ALSO a Zoho EXHIBITOR — so a company can carry
/// two Zoho ids.
///
/// IDs over names: the Zoho sponsor/exhibitor id is resolved ONCE by matching the
/// company name, then cached on <see cref="Domain.SponsorInfo.ZohoSponsorId"/> /
/// <c>ZohoExhibitorId</c>; subsequent syncs target by id (names change, ids don't).
/// All writes are UTF-8 (JsonContent) so Danish æøå survive. Fail-soft: the caller
/// always saves to SQL first; this only reports what synced.
/// </summary>
public sealed class SponsorZohoSyncService
{
    private readonly ZohoClient _zoho;
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorZohoSyncService> _log;

    // RULE (operator 2026-07-23): every CEH-made Zoho write must notify info@expertslive.dk
    // (the operator must publish/delete manually in Backstage). Optional so tests/legacy
    // constructions keep compiling; null ⇒ no notification.
    private readonly Email.ZohoChangeNotifier? _zohoChanges;

    public SponsorZohoSyncService(
        ZohoClient zoho, CommunityHubDbContext db, ZohoOptions options,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        ILogger<SponsorZohoSyncService> log,
        Email.ZohoChangeNotifier? zohoChanges = null)
    {
        _zoho = zoho;
        _db = db;
        _options = options;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
        _zohoChanges = zohoChanges;
    }

    /// <summary>
    /// §302 (operator 2026-07-24): <see cref="SponsorFields"/>/<see cref="ExhibitorFields"/>
    /// name the Zoho GUI fields a sync actually WROTE (e.g. "Description", "Website URL",
    /// "Contact Email") — empty/null means the PUT was skipped because Zoho already
    /// matched CEH ("if values are already set, then skip"), so callers put precise,
    /// change-only lines in the ops mail (no more a mail line on every pass).
    /// </summary>
    /// <param name="ManualLines">
    /// 🔴 §792 — the hand-entry lines for this company (blank or different in Zoho, with the CEH
    /// value to paste). Under Plan B this is where the content lives: <see cref="SponsorFields"/> and
    /// <see cref="ExhibitorFields"/> are now always empty, because nothing is written.
    ///
    /// <para>⚠️ It MUST be returned rather than only mailed in-line: the BULK paths call this with
    /// <c>notifyZohoChange: false</c> and compose ONE batched mail for the whole run. Without this
    /// the scheduled catch-up would have run silently — every line dropped on the floor — which is
    /// exactly the run he is relying on after the stamp flush.</para>
    /// </param>
    public sealed record SyncResult(
        bool Enabled, bool SponsorSynced, bool ExhibitorSynced, bool IsExhibitor, string? Error,
        IReadOnlyList<string>? SponsorFields = null, IReadOnlyList<string>? ExhibitorFields = null,
        IReadOnlyList<string>? ManualLines = null);

    /// <summary>
    /// Push one company's Company-Details fields to its Zoho sponsor/exhibitor records.
    /// <paramref name="notifyZohoChange"/> controls the operator-2026-07-23 CEH→Zoho change
    /// mail; bulk callers (provision / Migrate+Resync) pass <c>false</c> and send ONE
    /// batched mail for the whole run instead of one per company.
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        int eventId, string companyId, string companyName, CancellationToken ct = default,
        string? accessToken = null, bool notifyZohoChange = true)
    {
        if (!_options.Enabled)
            return new(false, false, false, false, null);

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
            return new(true, false, false, false, "Nothing to sync yet.");

        // 🔴 §1035 — the per-company gate. This method is the one EVERY caller funnels through
        // (the bulk re-sync, the order pull, the organizer buttons), so a test or withdrawn company
        // is refused once here rather than at each of them.
        if (!SponsorZohoScope.MayPushToZoho(info))
        {
            _log.LogInformation(
                "Zoho sync: {Co} skipped — {Reason} (§1035).",
                companyId, SponsorZohoScope.SkipReason(info));
            return new(true, false, false, info.HasBooth,
                $"Skipped — this company is {SponsorZohoScope.SkipReason(info)}.");
        }

        // Reuse a caller-supplied token (bulk re-sync fetches ONE token for the whole
        // run to avoid the Zoho token-endpoint rate limit); otherwise fetch our own.
        var token = accessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            try { token = await _zoho.GetAccessTokenAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Zoho sync: token request threw."); token = null; }
        }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, false, false, info.HasBooth, "Could not authenticate to Zoho Backstage.");

        var changedIds = false;
        var sponsorSynced = false;
        var exhibitorSynced = false;
        // §302: the Zoho GUI field names this sync actually wrote (change-only mails).
        var sponsorFields = new List<string>();
        var exhibitorFields = new List<string>();
        // §792 — Plan B: the fields to enter BY HAND in Backstage, one line each, with the CEH
        // value to copy. This replaces the API update entirely for sponsors + exhibitors.
        var manualLines = new List<string>();
        // Declared out here because the hand-entry mail is composed AFTER the try/catch — the mail
        // must still go out on the path where a later Zoho read threw.
        string? desiredEmail = null;
        var emailChanged = false;

        try
        {
            // FILL-BLANK reconcile (REQUIREMENTS §41b): pull blank CEH social/web fields
            // from the webshop, and push CEH values back to a blank webshop field. Runs
            // before the Zoho push so a freshly-pulled WebsiteUrl is sent on this same sync.
            if (await ReconcileWithWebshopAsync(info, ct)) changedIds = true;

            // The contact email is sent to Zoho ONLY when it actually CHANGED vs the last
            // value we pushed (Zoho hard-caps email updates at 3 — a no-op resend burns one).
            desiredEmail = NullIf(info.EventCoordinatorEmail);
            emailChanged = desiredEmail is not null
                && !string.Equals(desiredEmail, info.ZohoContactEmail, StringComparison.OrdinalIgnoreCase);

            // §553 — SELF-HEAL A DEAD LINK, before anything tries to use it. Operator was
            // emphatic: "i told you to include self-heal to detect that this backstage id doesn't
            // exist anymore … why don't you make it consistent across any comparison against zoho.
            // i do not accept workarounds manually". Sponsors/exhibitors had NO self-heal at all.
            if (await HealDeadLinksAsync(info, token!, ct)) changedIds = true;

            // §792.7 — the booth VIDEOS and COLLATERAL the sponsor has given us. Zoho has NO API
            // for either (every candidate endpoint 404s — /collaterals, /documents, /attachments,
            // /files), so unlike the fields above there is nothing to compare against and nothing
            // that could ever be pushed. They are pure hand-work, which is exactly why he asked for
            // them by mail: *"i must get booth collateral and videos by email also"* … *"if chg
            // happens"*.
            var materials = await _db.SponsorBoothMaterials
                .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId)
                .OrderBy(m => m.Kind).ThenBy(m => m.CreatedAt)
                .ToListAsync(ct);

            // §792 — ONE stamp for the whole hand-entry report, covering the sponsor AND exhibitor
            // fields together. Deliberately not two: he receives ONE mail per company, so two stamps
            // could report half of it and silently stamp the other half as "told him already".
            var reportHash = ManualReportHash(info, materials);
            var reportChanged = reportHash is not null
                && !string.Equals(reportHash, info.ZohoSponsorProfilePushedHash, StringComparison.Ordinal);

            // --- Sponsor record (all paying companies) ---
            if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                var sponsors = await _zoho.GetSponsorsAsync(token!, ct);
                var match = sponsors.FirstOrDefault(s => NameEq(s.CompanyName, companyName));
                if (match is not null) { info.ZohoSponsorId = match.Id; changedIds = true; }
            }
            if (!string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                // Zoho ← CEH fill-blank: only push fields that are BLANK in Zoho today.
                // §302 (operator 2026-07-24, the 70-mail night): when NOTHING would change
                // — nothing blank to fill, e-mail unchanged — SKIP the PUT entirely. The
                // old unconditional PUT (always carrying company/contact names) reported
                // "Updated sponsor record" on every 10-minute pass.
                var z = await _zoho.GetSponsorByIdAsync(token!, info.ZohoSponsorId!, ct);

                // 🔒 §596 — CHANGE-DRIVEN, NOT FILL-BLANK ONLY. DO NOT REVERT TO `BlankInZoho &&`.
                //
                // This read `BlankInZoho(z?.Description) && …`, so once Zoho held ANY description
                // the condition was false FOREVER and a CHANGED CEH description could never reach
                // the sponsor record. The operator hit exactly that (2026-07-28): he edited the
                // company description, the EXHIBITOR record updated — that side is already
                // change-driven, via the social hash — and the SPONSOR record still read
                // "My company overview".
                //
                // A HASH STAMP is used rather than a live char-compare even though the sponsor GET
                // DOES echo the description: Zoho reformats rich text, so a char-compare would
                // differ on every pass and re-push + re-mail forever — the §302 "70-mail night".
                // Stamping what we sent means at most ONE push per real CEH change.
                //
                // FILL-BLANK is KEPT as a second trigger so a value that never reached Zoho still
                // lands even when the stamp already matches.
                // 🔴🔴 §792 — PLAN B: CEH NO LONGER UPDATES SPONSORS OR EXHIBITORS OVER THE API.
                //
                // Operator 2026-08-04, after §791.3 proved the exhibitor endpoint accepts and
                // discards `company_social_pages`: *"then we need to change to plan B for updates in
                // zoho. Any api UPDATES related to sponsors and exhibitors must be sent to
                // info@expertslive.dk as mail, so I manually can update the records"* …
                // *"we will not spend more time on api UPDATES in zoho anymore until they fix it"*.
                //
                // ⇒ Every field that differs is REPORTED for hand-entry, exactly like the
                // create-only speakers area (§763). Nothing is PUT.
                //
                // 🔑 CREATE IS UNAFFECTED — `CreateSponsorAsync` / `CreateExhibitorAsync` still run,
                // contact details and all. He scoped this to UPDATES: *"this limitation is for both
                // exhibtors + sponsors when doing a UPDATE; not create"*.
                //
                // 🔴 THE TRAP PLAN B WALKS STRAIGHT INTO, AND THE REASON FOR THE STAMP.
                //
                // The natural implementation — "report every field that is blank in Zoho" — mails
                // him on EVERY sync pass, for ever, because a field he has not yet typed in by hand
                // stays blank by definition. That is the §302 "70-mail night" rebuilt in mail form,
                // and it would be worse than the API loop it replaces: this one reaches a human.
                //
                // 🔒 So the report is STAMP-GATED: one mail per real CEH change, not per pass. The
                // stamp's meaning changes with Plan B — it used to record "what CEH last PUSHED to
                // Zoho", and now records "what CEH last REPORTED to the operator". Same column,
                // different question, and the question is now one CEH can actually answer honestly.
                //
                // 🔑 THIS IS EXACTLY WHY HE ASKED FOR THE FLUSH: *"i need the hash reset once you
                // have deployed this so i tomorrow can catch up via manual cut/paste"*. Clearing the
                // stamp makes the next pass report EVERYTHING, which is the complete catch-up list.
                // Without the stamp there would be nothing to flush and no way to ask for a resend.
                if (reportChanged)
                {
                    // 🔴 §803 — THE SPONSOR RECORD IS PUSHED AGAIN. Operator 2026-08-04: *"now i need
                    // you t verify the update of the sponsor record also works via the sync routine:
                    // company description + web url"*. Measured on the live API, 2linkIT's sponsor
                    // record, originals restored:
                    //   description  → 200, read back CHANGED  ✅ writable
                    //   website_url  → 200, read back CHANGED  ✅ writable
                    //
                    // ⚠️ The sponsor record has NO social-pages field, so unlike the exhibitor there
                    // is nothing left here that the API cannot do — every remaining hand-entry line
                    // for a sponsor is the CONTACT (§791.5), which is a decision, not a limitation.
                    var wantsDescription = NeedsManualEntry(z?.Description, info.CompanyDescription);
                    var wantsWebsite = NeedsManualEntry(z?.WebsiteUrl, info.WebsiteUrl);

                    if (wantsDescription || wantsWebsite)
                    {
                        var pushed = await _zoho.UpdateSponsorAsync(
                            token!, info.ZohoSponsorId!,
                            description: wantsDescription ? info.CompanyDescription : null,
                            websiteUrl: wantsWebsite ? info.WebsiteUrl : null,
                            companyName: companyName,
                            ct);

                        if (pushed)
                        {
                            if (wantsDescription) sponsorFields.Add("Description");
                            if (wantsWebsite) sponsorFields.Add("Website");
                            sponsorSynced = true;
                        }
                        else
                        {
                            // A refused push still has to reach Backstage, so it falls back to a
                            // hand-entry line — with Zoho's own reason now in the log (§802.4(2)).
                            if (wantsDescription)
                                manualLines.Add(ManualField("Sponsor", companyName, "Description", info.CompanyDescription));
                            if (wantsWebsite)
                                manualLines.Add(ManualField("Sponsor", companyName, "Website", info.WebsiteUrl));
                        }
                    }
                }
            }

            // --- Exhibitor record (booth companies only) ---
            if (info.HasBooth)
            {
                if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
                {
                    var exhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
                    var match = exhibitors.FirstOrDefault(e => NameEq(e.CompanyName, companyName));
                    if (match is not null) { info.ZohoExhibitorId = match.Id; changedIds = true; }
                }
                if (!string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
                {
                    // 🔴 §784.13 — THE "§302d LIVE FACT" THAT USED TO BE ASSERTED HERE WAS FALSE, and
                    // it cost the operator every sponsor's LinkedIn URL.
                    //
                    // It claimed: "the exhibitor GET echoes ONLY website_url/booth/name/contact —
                    // company_overview + the social pages are ACCEPTED by the PUT but NEVER readable
                    // back". MEASURED against the live API on 2026-08-04, the GET returns:
                    //     "company_social_pages": { "linkedin": "https://www.linkedin.com/..." }
                    // …alongside company_overview. Both ARE echoed. The premise was simply wrong.
                    //
                    // On that false premise the code pushed social ONCE PER VALUE and remembered it
                    // in a CEH-side ZohoSocialPushedHash. 🔒 That hash recorded INTENT, NOT ARRIVAL:
                    // when a push did not land — and measurably some did not, while others did in
                    // the same run — the value was still marked "done" and NEVER SENT AGAIN. Ten of
                    // thirteen sponsors sat with LinkedIn in CEH, a hash saying "pushed", and an
                    // empty field in Zoho, indefinitely.
                    //
                    // ⇒ The hash is GONE. Social is now live-compared exactly like website_url —
                    // the one field in this block that has always worked, for exactly this reason.
                    // A value that fails to land stays blank in Zoho and is simply re-sent on the
                    // next pass, so the system self-heals instead of remembering a lie.
                    //
                    // ⚠️ Do NOT reintroduce a "push once" memo to reduce chatter. The re-push is
                    // idempotent and only fires while Zoho is actually blank; the moment it lands,
                    // the live comparison stops it. Chattiness was never the real cost here.
                    // 🔴🔴 §791.3 — AND THEN THE LIVE API SETTLED IT: `company_social_pages` CANNOT BE
                    // WRITTEN OVER v3 AT ALL. Four controlled PUTs against PROD on 2026-08-04:
                    //   1. social alone            → 200, the RESPONSE BODY ECHOES IT, read-back absent
                    //   2. social + website + name → 200, read-back absent
                    //   3. social with "facebook", the exact key from Zoho's own doc sample
                    //                              → 200, read-back absent
                    //   4. add "twitter" to 2linkIT, a record whose "linkedin" IS set
                    //                              → 200, read-back STILL only {"linkedin":…}
                    // Test 4 is the clincher: even on a record that HAS social, the API cannot add to
                    // it. The field is accepted, echoed and silently discarded — it behaves READ-ONLY.
                    // ⇒ The three exhibitors that show social got it from the Backstage GUI, never
                    // from CEH. He set Admin By Request's LinkedIn by hand, and the sync's X/Twitter
                    // push on the same record in the same window did NOT land — one record proving
                    // both halves.
                    //
                    // 🔒 SO THE PUSH IS SWITCHED OFF, and live-comparing made that necessary rather
                    // than optional: "re-send whenever Zoho reads back blank" against a field that
                    // can never read back non-blank means EVERY sponsor, EVERY pass, for ever. That
                    // is the log he pasted. The right answer to an unwritable field is not a better
                    // retry.
                    //
                    // 🔴🔴 §792 — PLAN B. Nothing below is PUT to Zoho; every difference becomes a
                    // line in the hand-entry mail. See the sponsor block above for his wording.
                    //
                    // ⚠️ Note what this costs and why he accepted it: `website_url` and
                    // `company_overview` DO write correctly over the API. They are reported rather
                    // than pushed anyway, because he asked for ONE rule — *"Any api UPDATES related
                    // to sponsors and exhibitors"* — and a half-manual field set is worse than a
                    // fully manual one: he would have to remember which half the hub still handles.
                    if (reportChanged)
                    {
                        var z = await _zoho.GetExhibitorByIdAsync(token!, info.ZohoExhibitorId!, ct);

                        // 🔴🔴 §801.2/§802.4(1) — PLAN B IS NARROWED TO THE FIELD THAT IS ACTUALLY
                        // BROKEN. Operator 2026-08-04: *"i am still not convinced that the zoho
                        // backend api is broken for all scenarios, like company description fields"*
                        // … *"it has been working for 2linkit before"*. He was right.
                        //
                        // Re-measured against live PROD, one field per call, originals restored:
                        //   company_overview          → 200, read back CHANGED   ✅ writable
                        //   company_short_description → 200, read back CHANGED   ✅ writable
                        //   website_url               → 200, read back CHANGED   ✅ writable
                        //   company_social_pages      → 200, silently DISCARDED  🔴 unwritable
                        //
                        // The single 400 seen anywhere was `shortDescription` is too long — a LENGTH
                        // limit Zoho names in the body (§802), not a per-record failure. So the three
                        // writable fields go back to being PUSHED, and only the social pages become
                        // hand-entry. Reporting a field CEH can set itself is asking him to do the
                        // hub's job.
                        var wantsWebsite = NeedsManualEntry(z?.WebsiteUrl, info.WebsiteUrl);
                        var wantsOverview = NeedsManualEntry(z?.Description, info.CompanyDescription);
                        // §801.2 — the GET DOES return the short description, so it is now compared
                        // on its own instead of riding on the overview's difference.
                        var wantsShort = NeedsManualEntry(z?.ShortDescription, info.CompanyDescriptionShort);

                        if (wantsWebsite || wantsOverview || wantsShort)
                        {
                            var pushed = await _zoho.UpdateExhibitorAsync(
                                token!, info.ZohoExhibitorId!,
                                companyOverview: wantsOverview ? info.CompanyDescription : null,
                                companyShortDescription: wantsShort ? info.CompanyDescriptionShort : null,
                                ct,
                                websiteUrl: wantsWebsite ? info.WebsiteUrl : null);

                            if (pushed)
                            {
                                if (wantsWebsite) exhibitorFields.Add("Website");
                                if (wantsOverview) exhibitorFields.Add("Company Overview");
                                if (wantsShort) exhibitorFields.Add("Company Short Description");
                                exhibitorSynced = true;
                            }
                            else
                            {
                                // ⚠️ A refused push becomes a hand-entry line rather than vanishing:
                                // the value still has to reach Backstage, and now he is told. The
                                // REASON is in the log — `UpdateExhibitorAsync` reads Zoho's body
                                // (§802.4(2)), which is where "`shortDescription` is too long" lives.
                                if (wantsWebsite)
                                    manualLines.Add(ManualField("Exhibitor", companyName, "Website", info.WebsiteUrl));
                                if (wantsOverview)
                                    manualLines.Add(ManualField("Exhibitor", companyName, "Company Overview", info.CompanyDescription));
                                if (wantsShort)
                                    manualLines.Add(ManualField("Exhibitor", companyName, "Company Short Description", info.CompanyDescriptionShort));
                            }
                        }

                        // 🔴 The social pages stay hand-entry: Zoho accepts the PUT and keeps
                        // nothing (§791.3, re-measured §801.2). This is the ONE field Plan B was
                        // ever right about — and the one every company has a value for, which is
                        // why the whole list looked broken.
                        if (NeedsManualEntry(z?.LinkedInUrl, info.LinkedInUrl))
                            manualLines.Add(ManualField("Exhibitor", companyName, "Company Social Pages → LinkedIn", info.LinkedInUrl));
                        if (NeedsManualEntry(z?.TwitterUrl, info.TwitterUrl))
                            manualLines.Add(ManualField("Exhibitor", companyName, "Company Social Pages → X/Twitter", info.TwitterUrl));
                    }
                }
            }

            // §792.7 — booth videos + collateral, listed whenever ANYTHING in this company's report
            // changed ("if chg happens"). ⚠️ They are listed IN FULL rather than "1 new video":
            // Backstage wants the whole set on the exhibitor's page, and a delta would leave him
            // reconstructing which ones are already there.
            if (reportChanged && materials.Count > 0)
            {
                foreach (var m in materials.Where(m => m.Kind == BoothMaterialKind.Video))
                    manualLines.Add(ManualField("Exhibitor", companyName, "Booth video", m.Url));

                foreach (var m in materials.Where(m => m.Kind == BoothMaterialKind.Collateral))
                    manualLines.Add(ManualField(
                        "Exhibitor", companyName,
                        $"Booth collateral{(string.IsNullOrWhiteSpace(m.FileName) ? "" : $" ({m.FileName})")}",
                        m.Url));
            }

            // §792 — the CONTACT details are an API update too, so they become mail like the rest.
            // ⚠️ Reported only when the e-mail actually CHANGED vs the last known value: Zoho's
            // 3-update cap is what made this field special, and re-listing an unchanged contact on
            // every catch-up would train him to skip the line that matters.
            if (emailChanged && reportChanged)
            {
                var who = string.Join(" ", new[]
                {
                    info.EventCoordinatorFirstName, info.EventCoordinatorLastName,
                }.Where(p => !string.IsNullOrWhiteSpace(p)));
                manualLines.Add(ManualField(
                    "Sponsor + Exhibitor", companyName, "Contact",
                    string.IsNullOrWhiteSpace(who) ? desiredEmail : $"{who} — {desiredEmail}"));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Zoho sync failed for company {Co}.", companyId);
            if (changedIds) { try { await _db.SaveChangesAsync(ct); } catch { /* best-effort */ } }
            return new(true, sponsorSynced, exhibitorSynced, info.HasBooth,
                "Zoho sync hit an error — your details are saved; please try Sync again.",
                sponsorFields, exhibitorFields, manualLines);
        }

        if (changedIds) await _db.SaveChangesAsync(ct);

        // 🔴 §792 — PLAN B: ONE ops mail per sync listing what to enter BY HAND, because CEH no
        // longer updates sponsors or exhibitors over the API at all.
        //
        // Operator 2026-08-04: *"Any api UPDATES related to sponsors and exhibitors must be sent to
        // info@expertslive.dk as mail, so I manually can update the records"* …
        // *"This is similar to the email we get for speaker updates"*.
        //
        // 🔒 `manualOnly: true` is the same flag the create-only SPEAKERS area uses (§763), and it
        // matters for the same reason: the default preamble says "the hub just wrote this, please
        // publish it", which would be a flat lie here. Nothing was written.
        //
        // ⚠️ Empty list ⇒ no mail (the notifier skips silently). So a sync where Zoho already
        // matches CEH stays quiet — the §302 "70-mail night" rule is unchanged.
        if (notifyZohoChange && _zohoChanges is not null && manualLines.Count > 0)
        {
            await _zohoChanges.NotifyAsync(
                "Sponsors / exhibitors", manualLines, ct,
                actionable: true,
                actionUrl: null, actionText: null,
                intro: $"<strong>{System.Net.WebUtility.HtmlEncode(companyName)}</strong> — copy each "
                       + "value below into the matching field in Backstage.",
                manualOnly: true);

            // 🔒 STAMP ONLY AFTER THE MAIL IS AWAY, and only for what was actually reported. The
            // notifier never throws, so a failed send cannot be detected here — but stamping before
            // composing would risk marking a company "told him" on a run that threw earlier, and
            // this stamp is the only thing standing between him and a mail every pass.
            //
            // ⚠️ The e-mail stamp moves with it: it used to record "pushed to Zoho", and now records
            // "listed for hand-entry". Nothing pushes it any more.
            var stamped = ManualReportHash(info);
            if (stamped is not null)
            {
                info.ZohoSponsorProfilePushedHash = stamped;
                if (emailChanged) info.ZohoContactEmail = desiredEmail;
                await _db.SaveChangesAsync(ct);
            }
        }

        string? error = null;
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            error = "Couldn't find a matching sponsor in Zoho Backstage by company name — align the name in Backstage and re-sync.";
        else if (info.HasBooth && string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            error = "Synced your sponsor record, but couldn't find a matching exhibitor in Zoho by company name.";

        return new(true, sponsorSynced, exhibitorSynced, info.HasBooth, error,
            sponsorFields, exhibitorFields, manualLines);
    }

    /// <summary>§302d: the stamp of the non-echoing exhibitor fields (overview, short
    /// description, LinkedIn, X) CEH last pushed — null when all are blank (nothing to
    /// push, never "changed").</summary>
    /// <summary>
    /// §596 — the stamp of the SPONSOR-record profile values (description + website), so the push
    /// fires once per real CEH change instead of once per pass. Deliberately SEPARATE from
    /// <see cref="SocialHash"/>: that one covers the EXHIBITOR record's own field set, and sharing
    /// a stamp would let an exhibitor-only edit suppress a sponsor push, or vice versa.
    /// </summary>
    /// <summary>
    /// §792.5 — mark these companies as REPORTED, so the hand-entry mail does not repeat every pass.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"stamp on the batched path too"*. The per-company path stamps
    /// inline, but both BULK paths pass <c>notifyZohoChange: false</c> and send one mail for the
    /// whole run — so nothing was stamped and the same list went out every 10 minutes. That is the
    /// §302 70-mail night, and this time it reaches a human inbox.</para>
    ///
    /// <para>🔒 <b>Only the companies whose lines were actually IN the mail.</b> Stamping the whole
    /// edition would silence companies he was never told about — the exact failure mode of the
    /// original <c>ZohoSocialPushedHash</c>, which recorded intent instead of delivery (§784.13).</para>
    ///
    /// <para>⚠️ The notifier never throws, so a failed send cannot be detected here. Stamping is
    /// therefore best-effort by construction; the recovery is the same one he already uses —
    /// <c>UPDATE SponsorInfos SET ZohoSponsorProfilePushedHash = NULL</c> re-reports everything.</para>
    /// </remarks>
    public async Task<int> StampManualReportAsync(
        int eventId, IReadOnlyCollection<string> companyIds, CancellationToken ct = default)
    {
        if (companyIds.Count == 0) return 0;

        var infos = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && companyIds.Contains(s.SponsorCompanyId))
            .ToListAsync(ct);

        // 🔒 §792.7 — the materials must be loaded here too, or the stamp would be computed from a
        // DIFFERENT payload than the one that decided to send the mail — and the two would then
        // never agree, so every pass would look changed and mail again.
        var materialsByCompany = (await _db.SponsorBoothMaterials
                .Where(m => m.EventId == eventId && companyIds.Contains(m.SponsorCompanyId))
                .ToListAsync(ct))
            .GroupBy(m => m.SponsorCompanyId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SponsorBoothMaterial>)g.ToList());

        var stamped = 0;
        foreach (var info in infos)
        {
            materialsByCompany.TryGetValue(info.SponsorCompanyId, out var mats);
            var hash = ManualReportHash(info, mats);
            if (hash is null) continue;
            info.ZohoSponsorProfilePushedHash = hash;
            stamped++;
        }

        if (stamped > 0) await _db.SaveChangesAsync(ct);
        return stamped;
    }

    /// <summary>
    /// §792.1 — does this field need typing into Backstage by hand: is Zoho <b>blank OR DIFFERENT</b>?
    /// </summary>
    /// <remarks>
    /// <para>🔴 Operator 2026-08-04: *"web url etc must also be chk"*. The first cut asked only
    /// <c>BlankInZoho</c>, which is the rule the old PUSH used — and it is the wrong rule for a
    /// hand-entry report. A STALE value in Zoho is not blank, so a company whose website changed
    /// would never appear, and the list would quietly certify a record that does not match CEH. His
    /// goal is *"so they match CEH values"*, and only a difference check can deliver that.</para>
    ///
    /// <para>⚠️ Comparison is deliberately forgiving, because a false positive on every sync is what
    /// killed the previous design: trimmed, case-insensitive, trailing slash ignored, and — for the
    /// long text fields — HTML tags stripped and whitespace collapsed, since Zoho REFORMATS rich
    /// text and would otherwise differ from CEH for ever on a value nobody changed (§596's "70-mail
    /// night" reasoning, applied to the mail instead of the PUT).</para>
    ///
    /// <para>🔒 A scheme difference (<c>http</c> vs <c>https</c>) IS reported — that is a real
    /// mismatch he wants to fix, not formatting noise.</para>
    /// </remarks>
    /// <remarks>
    /// §989: the normalization moved to the shared <see cref="RichTextCompare"/> — the session
    /// path needed the same answer and had a weaker private copy of it. Behaviour here is
    /// unchanged except that an editor-blank Zoho value ("&lt;p&gt;&amp;nbsp;&lt;/p&gt;") now
    /// counts as blank, which is what it looks like to him.
    /// </remarks>
    private static bool NeedsManualEntry(string? inZoho, string? inCeh)
    {
        if (string.IsNullOrWhiteSpace(inCeh)) return false;            // nothing to paste
        if (RichTextCompare.IsEffectivelyBlank(inZoho)) return true;   // blank in Zoho
        return !string.Equals(Comparable(inZoho), Comparable(inCeh), StringComparison.OrdinalIgnoreCase);

        // The trailing slash is a URL concern local to THIS call site (website/social fields),
        // so it is trimmed AFTER normalization rather than baked into the shared normalizer —
        // "<p>https://x.example/</p>" has to reach the slash with its tags already gone.
        static string Comparable(string? s) => RichTextCompare.Comparable(s).TrimEnd('/');
    }

    /// <summary>
    /// §792 — ONE hand-entry line: which Zoho record, which company, which GUI field, and the exact
    /// CEH value to paste.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>The VALUE is included, in full.</b> A line saying "Surveil's LinkedIn is missing"
    /// makes him go and look it up in CEH; a line carrying the URL is a copy and a paste. That is
    /// the entire difference between a report he can act on at 8am and one he defers.</para>
    ///
    /// <para>🔒 <b>HTML-encoded here</b>, because <see cref="Email.ZohoChangeNotifier.Build"/> trusts
    /// caller-composed lines (§558) so they may carry markup. These values are operator-entered
    /// company text — an ampersand in a company name, or a stray angle bracket in a description,
    /// would otherwise break the mail body.</para>
    ///
    /// <para>⚠️ Long descriptions are NOT truncated. A shortened value cannot be pasted, and a
    /// pasted-in truncation is worse than no line at all.</para>
    /// </remarks>
    private static string ManualField(string record, string company, string field, string? value) =>
        $"<strong>{System.Net.WebUtility.HtmlEncode(record)}</strong> · "
        + $"{System.Net.WebUtility.HtmlEncode(company)} · "
        + $"<em>{System.Net.WebUtility.HtmlEncode(field)}</em><br>"
        // §898 — a highlighted SPAN, not <code>. The value is usually a person's name, an e-mail or
        // a URL that he copies into Backstage; rendering "Laura Gulbe" in a monospace code face made
        // the mail look broken (operator 2026-08-06: *"font looks weird"*). The tint keeps the
        // copy-me boundary visible without pretending the value is source code.
        + $"<span style=\"word-break:break-all;background:#f6f8fa;padding:2px 6px;border-radius:3px;\">"
        + $"{System.Net.WebUtility.HtmlEncode(value ?? string.Empty)}</span>";

    /// <summary>
    /// §792 — the stamp of everything the hand-entry mail can report, so the operator gets ONE mail
    /// per real CEH change instead of one per sync pass.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>It must cover EVERY reportable field.</b> The old <c>SponsorProfileHash</c> covered
    /// only description + website, which was right when it gated a PUT of those two fields. Under
    /// Plan B the same stamp suppresses the whole mail, so a field left out of the hash would be a
    /// field he is never told about again after the first send — silently, and for ever.</para>
    ///
    /// <para><b>To force a complete re-report</b> (his 2026-08-04 catch-up):
    /// <c>UPDATE SponsorInfos SET ZohoSponsorProfilePushedHash = NULL</c>. The next sync then lists
    /// every field Zoho is missing, for every company.</para>
    ///
    /// <para>🔴 §804 — <b>THIS IS WHAT MAKES THE BOOTH MEDIA REACH HIM AT ALL.</b> Operator
    /// 2026-08-04: *"i must manually handle those and it must be included in the email so i am
    /// informed of any urls (video) and booth colleteral (including files) to upload"*. There is no
    /// Zoho API for either (re-proven §804.1: every sub-resource 404s and the exhibitor PUT answers
    /// <c>"Extra key found"</c>), so the mail is the ONLY delivery mechanism — and a video the hash
    /// did not cover would be a video he is never told about. <c>internal</c> so the property can be
    /// tested directly rather than inferred.</para>
    /// </remarks>
    internal static string? ManualReportHash(
        SponsorInfo info, IReadOnlyList<SponsorBoothMaterial>? materials = null)
    {
        var payload = string.Join("\n",
            info.CompanyDescription ?? string.Empty,
            info.CompanyDescriptionShort ?? string.Empty,
            info.WebsiteUrl ?? string.Empty,
            info.LinkedInUrl ?? string.Empty,
            info.TwitterUrl ?? string.Empty,
            info.EventCoordinatorFirstName ?? string.Empty,
            info.EventCoordinatorLastName ?? string.Empty,
            info.EventCoordinatorEmail ?? string.Empty);

        // 🔒 §792.7 — the booth materials are IN the hash, or "if chg happens" would never fire for
        // them: a sponsor adding a video changes nothing on SponsorInfo, so the stamp would still
        // match and the mail would never mention it. ORDERED so the hash is stable — a set that
        // hashes differently on each read would mail on every pass.
        if (materials is { Count: > 0 })
        {
            payload += "\n" + string.Join("\n", materials
                .Select(m => $"{m.Kind}|{m.Url}|{m.FileName}")
                .OrderBy(x => x, StringComparer.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(payload.Replace("\n", string.Empty))) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)))[..32];
    }

    private static string? SponsorProfileHash(SponsorInfo info)
    {
        var payload = string.Join("\n",
            info.CompanyDescription ?? string.Empty,
            info.WebsiteUrl ?? string.Empty);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)))[..32];
    }

    private static string? SocialHash(SponsorInfo info)
    {
        var payload = string.Join("\n",
            info.CompanyDescription ?? string.Empty,
            info.CompanyDescriptionShort ?? string.Empty,
            info.LinkedInUrl ?? string.Empty,
            info.TwitterUrl ?? string.Empty);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)))[..32];
    }

    public sealed record BoothSyncResult(
        bool Enabled, bool IsExhibitor, int AddedToZoho, int PulledFromZoho, string? Error);

    /// <summary>
    /// Reconcile this exhibitor's booth members with Zoho Backstage by EMAIL. The
    /// re-pull/create flow is add-only both ways: members present in Zoho but not in CEH
    /// are pulled INTO CEH; members in CEH but not in Zoho are created in Zoho
    /// (create-bulk). Matched members are flagged synced. Per-member DELETE is handled
    /// out-of-band by <see cref="DeleteBoothMemberAsync"/> (Zoho now supports member
    /// delete); the CEH tombstone still blocks re-pull so a removal can't resurrect.
    /// </summary>
    public async Task<BoothSyncResult> SyncBoothMembersAsync(
        int eventId, string companyId, string companyName, CancellationToken ct = default)
    {
        if (!_options.Enabled) return new(false, false, 0, 0, null);

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null) return new(true, false, 0, 0, "Nothing to sync yet.");
        if (!info.HasBooth) return new(true, false, 0, 0, "Booth members are for exhibitors only.");

        string? token;
        try { token = await _zoho.GetAccessTokenAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Booth sync: token request threw."); token = null; }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, true, 0, 0, "Could not authenticate to Zoho Backstage.");

        // Resolve + cache the exhibitor id.
        if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
        {
            try
            {
                var exhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
                var match = exhibitors.FirstOrDefault(e => NameEq(e.CompanyName, companyName));
                if (match is not null) { info.ZohoExhibitorId = match.Id; await _db.SaveChangesAsync(ct); }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Booth sync: exhibitor lookup failed for {Co}.", companyId); }
        }
        if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            return new(true, true, 0, 0, "Couldn't find a matching exhibitor in Zoho by company name.");

        try
        {
            var zoho = await _zoho.GetBoothMembersAsync(token!, info.ZohoExhibitorId!, ct);
            var zohoEmails = zoho.Select(z => z.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Active members reconcile both ways; tombstoned (soft-deleted) members are excluded
            // from CEH but their emails BLOCK the re-pull below — even though the hub now also
            // deletes the member in Zoho (DeleteBoothMemberAsync), the tombstone is belt-and-braces
            // so a removed member can never be resurrected by an add-only re-pull.
            var all = await _db.SponsorBoothMembers
                .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId)
                .ToListAsync(ct);
            var ceh = all.Where(m => m.DeletedAt == null).ToList();
            var cehEmails = ceh.Select(c => c.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tombstonedEmails = all.Where(m => m.DeletedAt != null)
                .Select(m => m.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Zoho-only → pull into CEH (but never re-pull a member the sponsor tombstoned).
            var pulled = 0;
            foreach (var z in zoho)
            {
                if (cehEmails.Contains(z.Email) || tombstonedEmails.Contains(z.Email)) continue;
                _db.SponsorBoothMembers.Add(new SponsorBoothMember
                {
                    EventId = eventId,
                    SponsorCompanyId = companyId,
                    FirstName = z.FirstName,
                    LastName = z.LastName,
                    Email = z.Email,
                    Role = z.Role.Contains("admin", StringComparison.OrdinalIgnoreCase)
                        ? BoothMemberRole.Admin : BoothMemberRole.Staff,
                    SyncedToZoho = true,
                });
                cehEmails.Add(z.Email);
                pulled++;
            }

            // CEH-only → create in Zoho.
            var toCreate = ceh.Where(c => !zohoEmails.Contains(c.Email)).ToList();
            var added = 0;
            if (toCreate.Count > 0)
            {
                var result = await _zoho.CreateBoothMembersAsync(
                    token!, info.ZohoExhibitorId!,
                    toCreate.Select(c => (
                        c.FirstName, c.LastName, c.Email,
                        c.Role == BoothMemberRole.Admin ? "ADMIN" : "staff",
                        (string?)companyName)).ToList(),
                    ct);
                if (!result.Ok)
                {
                    if (pulled > 0) await _db.SaveChangesAsync(ct);
                    return new(true, true, 0, pulled, "Couldn't create booth members in Zoho — please try Sync again.");
                }

                // 🔴 §793 — A 200 IS NOT "ALL CREATED". Measured against PROD 2026-08-04: Zoho
                // answers 200 with an EMPTY members array and a `skipped_emails` entry explaining
                // the refusal, e.g. *"The booth member limit has been reached. To add more, please
                // modify the Exhibitor Benefits."*
                //
                // 🔒 The refused ones must NOT be stamped SyncedToZoho. Stamping them is the
                // §784.13 failure in another costume: CEH would record a member as delivered, never
                // retry it, and show the sponsor a booth roster Zoho does not have. Left unstamped,
                // the next pass tries again — and succeeds the moment he raises the benefit limit.
                var refused = result.Skipped
                    .ToDictionary(s => s.Email, s => s.Reason, StringComparer.OrdinalIgnoreCase);

                foreach (var c in toCreate.Where(c => !refused.ContainsKey(c.Email)))
                    c.SyncedToZoho = true;

                added = toCreate.Count(c => !refused.ContainsKey(c.Email));

                if (refused.Count > 0)
                {
                    // Zoho's own words, carried to the human who can act on them — the limit is
                    // raised in Backstage under Exhibitor Benefits, which is not something CEH can
                    // do or work around.
                    var detail = string.Join("; ", refused.Select(r => $"{r.Key} — {r.Value}"));
                    _log.LogWarning(
                        "Booth members REFUSED by Zoho for {Company}: {Detail}", companyName, detail);

                    if (pulled > 0 || added > 0) await _db.SaveChangesAsync(ct);
                    return new(true, true, added, pulled,
                        $"Zoho refused {refused.Count} booth member(s): {detail}");
                }
            }

            // Flag matched members as synced.
            foreach (var c in ceh) if (zohoEmails.Contains(c.Email)) c.SyncedToZoho = true;

            await _db.SaveChangesAsync(ct);

            // Operator 2026-07-23: ONE batched ops mail per pass listing the booth members
            // CREATED in Zoho (a pull INTO CEH is not a Zoho write — nothing to publish).
            if (added > 0 && _zohoChanges is not null)
                await _zohoChanges.NotifyAsync("Sponsors / exhibitors",
                    toCreate.Select(c =>
                        $"Added booth member {c.FirstName} {c.LastName} ({c.Email}) to exhibitor '{companyName}'")
                        .ToList(), ct);

            return new(true, true, added, pulled, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Booth sync failed for company {Co}.", companyId);
            return new(true, true, 0, 0, "Booth member sync hit an error — please try again.");
        }
    }

    /// <summary>Outcome of a Zoho booth-member delete (REQUIREMENTS §41a/§56 — member only).</summary>
    public enum BoothMemberDeleteResult
    {
        /// <summary>The member was found in Zoho and deleted there.</summary>
        Deleted,
        /// <summary>No Zoho member matched the email (already gone / never synced) — nothing to delete.</summary>
        NotFoundInZoho,
        /// <summary>Zoho integration is disabled for this environment — no Zoho call made.</summary>
        SyncDisabled,
        /// <summary>Auth/HTTP/Zoho error (logged); the hub delete should still proceed.</summary>
        Error,
    }

    /// <summary>
    /// Delete ONE booth MEMBER from the company's Zoho exhibitor by email (REQUIREMENTS
    /// §41a/§56 — member delete ONLY; this never touches the exhibitor/sponsor RECORD).
    /// CEH stores the member's email (not the Zoho member id), so this resolves the
    /// company's <c>ZohoExhibitorId</c>, GETs the exhibitor's Zoho members, finds the one
    /// whose email matches (OrdinalIgnoreCase) and DELETEs it by its Zoho member id.
    /// Fail-soft: returns a <see cref="BoothMemberDeleteResult"/> and NEVER throws, so a
    /// Zoho failure can't break the caller's hub-side delete.
    /// </summary>
    public async Task<BoothMemberDeleteResult> DeleteBoothMemberAsync(
        int eventId, string companyId, string email, CancellationToken ct = default)
    {
        if (!_options.Enabled) return BoothMemberDeleteResult.SyncDisabled;
        if (string.IsNullOrWhiteSpace(email)) return BoothMemberDeleteResult.NotFoundInZoho;

        try
        {
            var info = await _db.SponsorInfos.FirstOrDefaultAsync(
                s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
            // No exhibitor id cached ⇒ the member was never synced to Zoho ⇒ nothing to delete.
            if (info is null || string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
                return BoothMemberDeleteResult.NotFoundInZoho;

            string? token = await _zoho.GetAccessTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                _log.LogWarning("Zoho member delete: could not authenticate (company {Co}).", companyId);
                return BoothMemberDeleteResult.Error;
            }

            var members = await _zoho.GetBoothMembersAsync(token!, info.ZohoExhibitorId!, ct);
            var match = members.FirstOrDefault(
                m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrWhiteSpace(match.Id))
                return BoothMemberDeleteResult.NotFoundInZoho;

            var ok = await _zoho.DeleteBoothMemberAsync(token!, info.ZohoExhibitorId!, match.Id, ct);

            // Operator 2026-07-23: a successful Zoho write (member DELETE) must notify the
            // ops mailbox so the exhibitor page can be re-published/pruned in Backstage.
            if (ok && _zohoChanges is not null)
                await _zohoChanges.NotifyAsync("Sponsors / exhibitors",
                    new[] { $"Deleted booth member {email} from exhibitor (company {companyId})" }, ct);

            return ok ? BoothMemberDeleteResult.Deleted : BoothMemberDeleteResult.Error;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Zoho member delete failed for company {Co} ({Email}).", companyId, email);
            return BoothMemberDeleteResult.Error;
        }
    }

    public sealed record BulkResult(
        int Companies, int CoordinatorsFilled, int SponsorsSynced, int ExhibitorsSynced,
        int Failed, List<string> Notes);

    /// <summary>
    /// One-time migration + full re-sync (operator 2026-06-24): for every sponsor
    /// company in the event, fill the Event Coordinator from the webshop default
    /// coordinator IF empty (CEH owns it once set — never overwrites a filled one),
    /// then push every record to Zoho Backstage (fields + UTF-8-correct contact),
    /// which fixes the mojibake the legacy PowerShell sync produced. Re-runnable.
    /// </summary>
    public async Task<BulkResult> MigrateCoordinatorsAndResyncAsync(
        int eventId, CancellationToken ct = default)
    {
        var infos = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);

        // Fetch ONE Zoho token for the whole run (avoids per-company token rate-limit).
        string? token = null;
        if (_options.Enabled)
        {
            try { token = await _zoho.GetAccessTokenAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Bulk re-sync: token request threw."); }
        }

        int filled = 0, sponsorsSynced = 0, exhibitorsSynced = 0, failed = 0;
        var notes = new List<string>();
        // Operator 2026-07-23: collect every SUCCESSFUL Zoho write for ONE batched ops mail
        // (per-company notify is suppressed below — batch-per-run, never per item).
        var zohoWrites = new List<string>();
        // §792 — hand-entry lines for the whole run, batched into ONE mail at the end.
        var runManualLines = new List<string>();
        // §792.5 — WHICH companies contributed those lines, so only they are stamped as reported.
        var reportedCompanyIds = new List<string>();

        foreach (var info in infos)
        {
            string name = info.SponsorCompanyId;
            CompanyManagerCompany? company = null;
            if (_cmOptions.Enabled && int.TryParse(info.SponsorCompanyId, out var cid))
            {
                try { company = await _cm.GetCompanyAsync(cid, ct); } catch { /* fail-soft */ }
                if (company is not null)
                    name = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;

                // §597.4 — ONE rule for the coordinator, shared with the per-company path: follow
                // CM's DEFAULT pointer when it changes, fill blanks when it has not. This was a
                // fill-blank-only migrate, which meant CEH took the coordinator once and could never
                // notice him changing the default in CM afterwards.
                if (company is not null && await SyncDefaultCoordinatorAsync(info, company, cid, ct))
                {
                    await _db.SaveChangesAsync(ct);
                    filled++;
                }
            }

            var r = await SyncAsync(eventId, info.SponsorCompanyId, name, ct,
                accessToken: token, notifyZohoChange: false);
            // §302: change-only, field-named lines — an all-set company adds NO line.
            //
            // 🔴 §791.2 — "PUSHED TO", NOT "UPDATED". The old wording stated an outcome the code
            // does not know: the PUT returns success and Zoho can still DISCARD the field, which is
            // exactly what `company_social_pages` did through three sessions of §784.13 before
            // §791.3 measured it. *"Updated exhibitor 'Surveil'"* read as ARRIVED and has only ever
            // meant SENT. One honest word would have made that thread one run long instead of three.
            if (r.SponsorSynced && r.SponsorFields is { Count: > 0 })
            { sponsorsSynced++; zohoWrites.Add($"Pushed to sponsor '{name}' — Zoho GUI fields: {string.Join(", ", r.SponsorFields)}"); }
            if (r.ExhibitorSynced && r.ExhibitorFields is { Count: > 0 })
            { exhibitorsSynced++; zohoWrites.Add($"Pushed to exhibitor '{name}' — Zoho GUI fields: {string.Join(", ", r.ExhibitorFields)}"); }
            // 🔴 §792 — collect the hand-entry lines. Plan B writes nothing, so the two branches
            // above no longer fire for sponsors/exhibitors; without this the bulk re-sync would be
            // silent and the catch-up mail would never arrive.
            if (r.ManualLines is { Count: > 0 })
            {
                runManualLines.AddRange(r.ManualLines);
                reportedCompanyIds.Add(info.SponsorCompanyId);
            }
            if (r.Error is not null) { failed++; notes.Add($"{name}: {r.Error}"); }
        }

        // Operator 2026-07-23: ONE batched ops mail for the whole re-sync run.
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Sponsors / exhibitors", zohoWrites, ct);

        // §792 — and ONE batched hand-entry mail: every field, every company, in a single list.
        if (_zohoChanges is not null && runManualLines.Count > 0)
        {
            await _zohoChanges.NotifyAsync(
                "Sponsors / exhibitors", runManualLines, ct,
                actionable: true, actionUrl: null, actionText: null,
                intro: "Zoho Backstage <strong>ignores API updates</strong> for these fields "
                       + "(measured 2026-08-04, §791.3), so CEH no longer tries. Copy each value "
                       + "below into the matching field in Backstage so Zoho matches CEH.",
                manualOnly: true);

            // 🔒 §792.5 (operator 2026-08-04: *"stamp on the batched path too"*) — STAMP AFTER THE
            // MAIL, and only the companies that were in it. Without this the identical list goes out
            // every 10 minutes for ever, because a field he has not yet typed in still does not
            // match on the next pass.
            var stamped = await StampManualReportAsync(eventId, reportedCompanyIds, ct);
            _log.LogInformation(
                "Bulk re-sync: hand-entry mail sent with {Lines} line(s) across {Companies} "
                + "company(ies); {Stamped} stamped as reported (flush the stamp to re-report).",
                runManualLines.Count, reportedCompanyIds.Count, stamped);
        }

        return new BulkResult(infos.Count, filled, sponsorsSynced, exhibitorsSynced, failed, notes);
    }

    /// <summary>
    /// FILL-BLANK reconcile of social/web fields between the webshop (Company Manager)
    /// and CEH (SponsorInfo) — REQUIREMENTS §41b. NEVER overwrites a non-blank value.
    ///   • CEH ← webshop: a field blank in CEH is pulled from the webshop company
    ///     (website/linkedin/twitter; description is CEH-only on the webshop side).
    ///   • webshop ← CEH: a field blank in the webshop is pushed from CEH.
    /// Returns true if any CEH field changed (caller persists). Description has no
    /// webshop counterpart, so only the three URLs reconcile with the webshop.
    /// </summary>
    private async Task<bool> ReconcileWithWebshopAsync(Domain.SponsorInfo info, CancellationToken ct)
    {
        if (!_cmOptions.Enabled || !int.TryParse(info.SponsorCompanyId, out var cid)) return false;

        CompanyManagerCompany? company;
        try { company = await _cm.GetCompanyAsync(cid, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Reconcile: GetCompany failed for {Co}.", info.SponsorCompanyId); return false; }
        if (company is null) return false;

        var cehChanged = false;

        // §597.4 — follow CM's DEFAULT EVENT COORDINATOR. Runs on every sync, not just the
        // one-time migrate, because he changes the default in CM and CEH has to notice.
        if (await SyncDefaultCoordinatorAsync(info, company, cid, ct)) cehChanged = true;

        // CEH ← webshop: fill a blank CEH field from the webshop company.
        if (string.IsNullOrWhiteSpace(info.WebsiteUrl) && !string.IsNullOrWhiteSpace(company.WebsiteUrl))
        { info.WebsiteUrl = company.WebsiteUrl.Trim(); cehChanged = true; }
        if (string.IsNullOrWhiteSpace(info.LinkedInUrl) && !string.IsNullOrWhiteSpace(company.LinkedInUrl))
        { info.LinkedInUrl = company.LinkedInUrl.Trim(); cehChanged = true; }
        if (string.IsNullOrWhiteSpace(info.TwitterUrl) && !string.IsNullOrWhiteSpace(company.TwitterUrl))
        { info.TwitterUrl = company.TwitterUrl.Trim(); cehChanged = true; }

        // webshop ← CEH: push a CEH value to a blank webshop field (only the keys we fill).
        var push = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(info.WebsiteUrl) && string.IsNullOrWhiteSpace(company.WebsiteUrl))
            push["web_address"] = info.WebsiteUrl;
        if (!string.IsNullOrWhiteSpace(info.LinkedInUrl) && string.IsNullOrWhiteSpace(company.LinkedInUrl))
            push["linkedin_url"] = info.LinkedInUrl;
        if (!string.IsNullOrWhiteSpace(info.TwitterUrl) && string.IsNullOrWhiteSpace(company.TwitterUrl))
            push["twitter_url"] = info.TwitterUrl;
        if (push.Count > 0)
        {
            try { await _cm.UpdateCompanyAsync(cid, push, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Reconcile: UpdateCompany failed for {Co}.", info.SponsorCompanyId); }
        }

        return cehChanged;
    }

    /// <summary>
    /// §597.4 — carry Company Manager's <b>Default Event Coordinator</b> through to
    /// <c>SponsorInfo.EventCoordinator*</c>. Returns true when CEH values changed.
    /// </summary>
    /// <remarks>
    /// <para>His rule, in his words: *"default comes from CM (default event coordinator)"*, and
    /// *"the DEFAULT decides only the Zoho record"* — everyone with coordinator status still gets
    /// the mails (§597.3).</para>
    ///
    /// <para><b>Two ownerships, reconciled by the POINTER.</b> The values are CEH's once a human
    /// edits them on Company Details; the CHOICE of which contact is default is CM's. So:</para>
    /// <list type="bullet">
    /// <item><b>Pointer CHANGED</b> (or first ever read with nothing stored) ⇒ he made a deliberate
    /// decision in CM. Re-read the person and overwrite. This is the case the old fill-blank-only
    /// code could never see, and §597.4's whole remaining ◻.</item>
    /// <item><b>Pointer UNCHANGED</b> ⇒ fill blanks only, exactly as before. A hub edit is never
    /// silently reverted by a routine sync.</item>
    /// <item><b>Pointer CLEARED in CM</b> (0/absent) ⇒ change NOTHING. An unset default is not an
    /// instruction to wipe the contact, and wiping it would push a blank contact to Zoho.</item>
    /// </list>
    ///
    /// <para>🔒 The <see cref="Domain.SponsorInfo.ZohoContactEmail"/> guard is deliberately NOT
    /// touched here. Zoho caps contact-e-mail updates at 3 and a burnt cap loses an exhibitor's
    /// leads (§596.1) — so this decides WHAT the contact is, and that guard alone still decides
    /// whether an e-mail is worth spending an attempt on.</para>
    ///
    /// <para>Fail-soft throughout: a CM outage must leave the existing contact standing, never
    /// blank it.</para>
    /// </remarks>
    /// <summary>
    /// §597.4 — the decision alone, as a pure rule so it can be tested without Company Manager,
    /// Zoho or a database. Should CEH (re-)read the default coordinator from CM?
    /// </summary>
    /// <param name="storedPointer">The CM user id CEH last recorded as the default, or null.</param>
    /// <param name="cmPointer">The company's current CM default-coordinator user id (0 = unset).</param>
    /// <param name="cehCoordinatorEmpty">True when CEH holds no coordinator at all.</param>
    public static bool ShouldReadCoordinator(int? storedPointer, int cmPointer, bool cehCoordinatorEmpty)
    {
        // 🔒 Unset in CM is NOT an instruction to wipe. Blanking the contact here would push an
        // empty contact to Zoho and spend one of the 3 capped e-mail attempts doing it (§596.1).
        if (cmPointer <= 0) return false;

        // He changed the default in CM — a deliberate decision, and the case the old
        // fill-blank-only code could never see.
        if (storedPointer != cmPointer) return true;

        // Same default as last time: fill only if CEH has nothing, so a hub edit on Company Details
        // is never silently reverted by a routine sync.
        return cehCoordinatorEmpty;
    }

    private async Task<bool> SyncDefaultCoordinatorAsync(
        Domain.SponsorInfo info, CompanyManagerCompany company, int cid, CancellationToken ct)
    {
        var pointer = company.EventCoordinationDefaultContactUserId;
        var pointerChanged = pointer > 0 && info.CmDefaultCoordinatorUserId != pointer;

        if (!ShouldReadCoordinator(info.CmDefaultCoordinatorUserId, pointer, CoordinatorEmpty(info)))
            return false;

        CompanyManagerCoordinator? coord;
        try { coord = await _cm.GetDefaultCoordinatorAsync(cid, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "§597.4: could not resolve the default coordinator for company {Co} — the existing "
                + "contact is left standing.", info.SponsorCompanyId);
            return false;
        }

        // Resolved to nothing (an orphaned pointer). Same rule: never blank a good contact.
        if (coord is null || string.IsNullOrWhiteSpace(coord.Email))
        {
            _log.LogWarning(
                "§597.4: company {Co} points at CM user {UserId} as default event coordinator, but "
                + "that user resolved to no usable contact — CEH left unchanged.",
                info.SponsorCompanyId, pointer);
            return false;
        }

        var before = info.EventCoordinatorEmail;

        info.EventCoordinatorFirstName = NullIf(coord.FirstName);
        info.EventCoordinatorLastName = NullIf(coord.LastName);
        info.EventCoordinatorEmail = NullIf(coord.Email);
        info.EventCoordinatorPhone = NullIf(coord.Phone);
        info.EventCoordinatorCompanyName = NullIf(coord.CompanyName);
        info.CmDefaultCoordinatorUserId = pointer;

        if (pointerChanged && !string.IsNullOrWhiteSpace(before)
            && !string.Equals(before, info.EventCoordinatorEmail, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation(
                "§597.4: company {Co} default event coordinator changed in Company Manager — contact "
                + "moved from {Before} to {After} (CM user {UserId}).",
                info.SponsorCompanyId, before, info.EventCoordinatorEmail, pointer);
        }

        return true;
    }

    /// <summary>
    /// §553 — clear a stored Zoho sponsor/exhibitor id that a DEFINITE read says is gone, so the
    /// next pass re-creates the record. Returns true when a link was cleared.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap this closes.</b> §553 audited the self-heal across the integration and found
    /// it uneven: *"Speakers — YES. **Sponsors/exhibitors — NO self-heal at all.** Nothing clears a
    /// stale id."* A sponsor deleted in Zoho therefore left CEH pointing at a dead id forever —
    /// every sync updating nothing, reporting success, and never re-creating the record.</para>
    ///
    /// <para>🔒 <b>Cleared ONLY on `Gone`, which here means a literal 404 for THAT id.</b> Not on
    /// 401, not on 500, not on a timeout. §553's rule: *"an outage, an expired token or a missing
    /// API scope would look exactly like 'everything was deleted', and the healer would helpfully
    /// unlink the entire agenda."* A per-record 404 is the strongest evidence this API offers —
    /// stronger than the list-based probe sessions use, since there is no incomplete-page risk.</para>
    ///
    /// <para><b>Why re-creating is safe HERE but needed approval for sessions (§559).</b> A 404
    /// means there is nothing left to duplicate. The session case was different: its "gone" verdict
    /// came from a LIST read, where an incomplete page could invent a false absence, and the
    /// sessions API cannot delete a duplicate afterwards.</para>
    ///
    /// <para>⚠️ <b>Unknown must be LOUD, never swallowed</b> — the bare <c>catch { }</c> around the
    /// old session self-heal is how nine sessions stayed invisibly unlinked for months.</para>
    /// </remarks>
    private async Task<bool> HealDeadLinksAsync(
        Domain.SponsorInfo info, string token, CancellationToken ct)
    {
        var healed = false;

        if (!string.IsNullOrWhiteSpace(info.ZohoSponsorId))
        {
            var (_, link) = await _zoho.ProbeSponsorAsync(token, info.ZohoSponsorId!, ct);
            if (link.IsGone)
            {
                _log.LogWarning(
                    "§553 self-heal: company {Co} pointed at Zoho sponsor {Id}, which no longer "
                    + "exists ({Detail}). Link CLEARED — it will be re-created on the next pass.",
                    info.SponsorCompanyId, info.ZohoSponsorId, link.Detail);
                info.ZohoSponsorId = null;
                // The contact stamp belonged to the DELETED record. Keeping it would make the
                // re-created sponsor look like it already had the right contact e-mail, so the
                // create would skip sending it (§596.1) and the new record would have none.
                info.ZohoContactEmail = null;
                // Likewise the §596 sponsor-profile stamp: it records what was pushed to the
                // record that no longer exists, so leaving it would suppress the re-push.
                info.ZohoSponsorProfilePushedHash = null;
                healed = true;
            }
            else if (link.IsUnknown)
            {
                _log.LogWarning(
                    "§553 self-heal: could not verify the Zoho sponsor link for company {Co} "
                    + "({Detail}). NOTHING was changed.", info.SponsorCompanyId, link.Detail);
            }
        }

        if (!string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
        {
            var (_, link) = await _zoho.ProbeExhibitorAsync(token, info.ZohoExhibitorId!, ct);
            if (link.IsGone)
            {
                _log.LogWarning(
                    "§553 self-heal: company {Co} pointed at Zoho exhibitor {Id}, which no longer "
                    + "exists ({Detail}). Link CLEARED — it will be re-created on the next pass.",
                    info.SponsorCompanyId, info.ZohoExhibitorId, link.Detail);
                info.ZohoExhibitorId = null;
                // The social hash described the deleted record; keeping it would suppress the
                // re-push of description/socials to the replacement (§302d).
                info.ZohoSocialPushedHash = null;
                healed = true;
            }
            else if (link.IsUnknown)
            {
                _log.LogWarning(
                    "§553 self-heal: could not verify the Zoho exhibitor link for company {Co} "
                    + "({Detail}). NOTHING was changed.", info.SponsorCompanyId, link.Detail);
            }
        }

        return healed;
    }

    /// <summary>A Zoho field is "blank" (safe to fill) when it is null/empty/whitespace.</summary>
    private static bool BlankInZoho(string? zohoValue) => string.IsNullOrWhiteSpace(zohoValue);

    private static bool CoordinatorEmpty(Domain.SponsorInfo i) =>
        string.IsNullOrWhiteSpace(i.EventCoordinatorFirstName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorLastName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorEmail)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorPhone);

    private static string? NullIf(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool NameEq(string a, string b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
