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
    public sealed record SyncResult(
        bool Enabled, bool SponsorSynced, bool ExhibitorSynced, bool IsExhibitor, string? Error,
        IReadOnlyList<string>? SponsorFields = null, IReadOnlyList<string>? ExhibitorFields = null);

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

        try
        {
            // FILL-BLANK reconcile (REQUIREMENTS §41b): pull blank CEH social/web fields
            // from the webshop, and push CEH values back to a blank webshop field. Runs
            // before the Zoho push so a freshly-pulled WebsiteUrl is sent on this same sync.
            if (await ReconcileWithWebshopAsync(info, ct)) changedIds = true;

            // The contact email is sent to Zoho ONLY when it actually CHANGED vs the last
            // value we pushed (Zoho hard-caps email updates at 3 — a no-op resend burns one).
            var desiredEmail = NullIf(info.EventCoordinatorEmail);
            var emailChanged = desiredEmail is not null
                && !string.Equals(desiredEmail, info.ZohoContactEmail, StringComparison.OrdinalIgnoreCase);

            // §553 — SELF-HEAL A DEAD LINK, before anything tries to use it. Operator was
            // emphatic: "i told you to include self-heal to detect that this backstage id doesn't
            // exist anymore … why don't you make it consistent across any comparison against zoho.
            // i do not accept workarounds manually". Sponsors/exhibitors had NO self-heal at all.
            if (await HealDeadLinksAsync(info, token!, ct)) changedIds = true;

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
                var profileHash = SponsorProfileHash(info);
                var profileChanged = profileHash is not null
                    && !string.Equals(profileHash, info.ZohoSponsorProfilePushedHash, StringComparison.Ordinal);

                var sendDesc = !string.IsNullOrWhiteSpace(info.CompanyDescription)
                               && (profileChanged || BlankInZoho(z?.Description));
                var sendWeb = !string.IsNullOrWhiteSpace(info.WebsiteUrl)
                              && (profileChanged || BlankInZoho(z?.WebsiteUrl));
                if (sendDesc || sendWeb || emailChanged)
                {
                    // ⚠️ §596.1 — THE CONTACT BLOCK IS STILL GATED ON `emailChanged` ALONE.
                    // Zoho hard-caps sponsor e-mail updates at 3 and even a NO-OP RESEND burns one;
                    // exceeding it makes the sponsor AND exhibitor objects unupdatable, recoverable
                    // only by DELETING them — which loses their leads. A description push must
                    // therefore never drag the contact details along. That is precisely what the
                    // operator asked for: "we could send partly of the update like description
                    // without having to send the whole incl. sponsor contact details".
                    sponsorSynced = await _zoho.UpdateSponsorAsync(
                        token!, info.ZohoSponsorId!,
                        description: sendDesc ? info.CompanyDescription : null,
                        websiteUrl: sendWeb ? info.WebsiteUrl : null,
                        companyName: companyName, ct,
                        contactFirstName: emailChanged ? info.EventCoordinatorFirstName : null,
                        contactLastName: emailChanged ? info.EventCoordinatorLastName : null,
                        contactEmail: emailChanged ? desiredEmail : null);
                    if (sponsorSynced)
                    {
                        if (sendDesc) sponsorFields.Add("Description");
                        if (sendWeb) sponsorFields.Add("Website");
                        if (emailChanged) sponsorFields.Add("Contact Email");
                        // Stamp only on SUCCESS — a failed PUT must be retried, not forgotten.
                        if ((sendDesc || sendWeb) && profileHash is not null)
                        {
                            info.ZohoSponsorProfilePushedHash = profileHash;
                            changedIds = true;   // persisted with the other id/stamp writes below
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
                    // §302d LIVE FACT (operator's perpetual "Social Pages" mails): the
                    // exhibitor GET echoes ONLY website_url/booth/name/contact —
                    // company_overview + the social pages are ACCEPTED by the PUT but
                    // NEVER readable back, so a blank-in-Zoho check re-pushed + re-mailed
                    // them every pass. Website stays live-compared; the non-echoing group
                    // is pushed ONCE PER VALUE via the CEH-side ZohoSocialPushedHash
                    // stamp (re-pushed only when the CEH values actually change).
                    var z = await _zoho.GetExhibitorByIdAsync(token!, info.ZohoExhibitorId!, ct);
                    var sendWeb = BlankInZoho(z?.WebsiteUrl) && !string.IsNullOrWhiteSpace(info.WebsiteUrl);
                    var socialHash = SocialHash(info);
                    var socialChanged = socialHash is not null
                        && !string.Equals(socialHash, info.ZohoSocialPushedHash, StringComparison.Ordinal);
                    if (sendWeb || socialChanged || emailChanged)
                    {
                        exhibitorSynced = await _zoho.UpdateExhibitorAsync(
                            token!, info.ZohoExhibitorId!,
                            companyOverview: socialChanged ? info.CompanyDescription : null,
                            companyShortDescription: socialChanged ? info.CompanyDescriptionShort : null,
                            ct,
                            companyName: companyName,
                            contactFirstName: emailChanged ? info.EventCoordinatorFirstName : null,
                            contactLastName: emailChanged ? info.EventCoordinatorLastName : null,
                            websiteUrl: sendWeb ? info.WebsiteUrl : null,
                            linkedInUrl: socialChanged ? info.LinkedInUrl : null,
                            twitterUrl: socialChanged ? info.TwitterUrl : null,
                            contactEmail: emailChanged ? desiredEmail : null,
                            contactMobile: emailChanged ? info.EventCoordinatorPhone : null);
                        if (exhibitorSynced)
                        {
                            if (sendWeb) exhibitorFields.Add("Website");
                            if (socialChanged)
                            {
                                if (!string.IsNullOrWhiteSpace(info.CompanyDescription)) exhibitorFields.Add("Company Overview");
                                if (!string.IsNullOrWhiteSpace(info.LinkedInUrl)) exhibitorFields.Add("Social Pages (LinkedIn)");
                                if (!string.IsNullOrWhiteSpace(info.TwitterUrl)) exhibitorFields.Add("Social Pages (X/Twitter)");
                                info.ZohoSocialPushedHash = socialHash;
                                changedIds = true;
                            }
                            if (emailChanged) exhibitorFields.Add("Contact Email");
                        }
                    }
                }
            }

            // Stamp the email we just pushed so a future no-op sync won't re-send it
            // (only when an email-changing update actually succeeded).
            if (emailChanged && (sponsorSynced || exhibitorSynced))
            {
                info.ZohoContactEmail = desiredEmail;
                changedIds = true;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Zoho sync failed for company {Co}.", companyId);
            if (changedIds) { try { await _db.SaveChangesAsync(ct); } catch { /* best-effort */ } }
            return new(true, sponsorSynced, exhibitorSynced, info.HasBooth,
                "Zoho sync hit an error — your details are saved; please try Sync again.",
                sponsorFields, exhibitorFields);
        }

        if (changedIds) await _db.SaveChangesAsync(ct);

        // Operator 2026-07-23: ONE ops mail per sync listing what was actually written
        // to Zoho (publish/delete is manual in Backstage). Skipped when nothing synced.
        if (notifyZohoChange && _zohoChanges is not null)
        {
            // §302: name the Zoho GUI fields that actually changed — no fields, no line.
            var writes = new List<string>();
            if (sponsorSynced && sponsorFields.Count > 0)
                writes.Add($"Updated sponsor '{companyName}' — Zoho GUI fields: {string.Join(", ", sponsorFields)} (Zoho id {info.ZohoSponsorId})");
            if (exhibitorSynced && exhibitorFields.Count > 0)
                writes.Add($"Updated exhibitor '{companyName}' — Zoho GUI fields: {string.Join(", ", exhibitorFields)} (Zoho id {info.ZohoExhibitorId})");
            await _zohoChanges.NotifyAsync("Sponsors / exhibitors", writes, ct);
        }

        string? error = null;
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            error = "Couldn't find a matching sponsor in Zoho Backstage by company name — align the name in Backstage and re-sync.";
        else if (info.HasBooth && string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            error = "Synced your sponsor record, but couldn't find a matching exhibitor in Zoho by company name.";

        return new(true, sponsorSynced, exhibitorSynced, info.HasBooth, error,
            sponsorFields, exhibitorFields);
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
                var ok = await _zoho.CreateBoothMembersAsync(
                    token!, info.ZohoExhibitorId!,
                    toCreate.Select(c => (
                        c.FirstName, c.LastName, c.Email,
                        c.Role == BoothMemberRole.Admin ? "ADMIN" : "staff",
                        (string?)companyName)).ToList(),
                    ct);
                if (!ok)
                {
                    if (pulled > 0) await _db.SaveChangesAsync(ct);
                    return new(true, true, 0, pulled, "Couldn't create booth members in Zoho — please try Sync again.");
                }
                foreach (var c in toCreate) c.SyncedToZoho = true;
                added = toCreate.Count;
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
            if (r.SponsorSynced && r.SponsorFields is { Count: > 0 })
            { sponsorsSynced++; zohoWrites.Add($"Updated sponsor '{name}' — Zoho GUI fields: {string.Join(", ", r.SponsorFields)}"); }
            if (r.ExhibitorSynced && r.ExhibitorFields is { Count: > 0 })
            { exhibitorsSynced++; zohoWrites.Add($"Updated exhibitor '{name}' — Zoho GUI fields: {string.Join(", ", r.ExhibitorFields)}"); }
            if (r.Error is not null) { failed++; notes.Add($"{name}: {r.Error}"); }
        }

        // Operator 2026-07-23: ONE batched ops mail for the whole re-sync run.
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Sponsors / exhibitors", zohoWrites, ct);

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
