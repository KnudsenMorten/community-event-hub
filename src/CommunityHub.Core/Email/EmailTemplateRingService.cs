using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>One template's effective release ring, and where it came from.</summary>
/// <param name="TemplateKey">The template file key, e.g. <c>welcome-speaker</c>.</param>
/// <param name="Title">Friendly name for the Settings page.</param>
/// <param name="Audience">Which role section it files under (§515).</param>
/// <param name="FeatureKey">The feature whose ring applies when there is no override.</param>
/// <param name="FeatureRing">That feature's ring.</param>
/// <param name="OverrideRing">The per-template ring when the operator has set one.</param>
public sealed record EmailTemplateRingState(
    string TemplateKey,
    string Title,
    EmailAudience Audience,
    string FeatureKey,
    Ring FeatureRing,
    Ring? OverrideRing)
{
    /// <summary>
    /// The ring that governs THIS mail — its OWN ring, or <see cref="Ring.Ring0"/> when it has none.
    /// </summary>
    /// <remarks>
    /// 🔒 §705.2 — was <c>OverrideRing ?? FeatureRing</c>. The feature fallback is GONE (operator
    /// 2026-07-29: *"i want INDIVIDUAL EMAIL RING GATES - one for each !"*), and a mail with no ring
    /// FAILS CLOSED rather than inheriting one. This mirrors
    /// <see cref="EmailTemplateRingService.GetEffectiveRingAsync"/> exactly, so <b>the page can never
    /// show an audience the gate would not honour</b> — which is the §326bx/§563 defect that cost the
    /// operator's confidence in this page in the first place.
    /// </remarks>
    public Ring EffectiveRing => OverrideRing ?? Ring.Ring0;

    /// <summary>True when this mail has a ring of its own (i.e. it is not failing closed).</summary>
    public bool IsOverridden => OverrideRing.HasValue;

    /// <summary>
    /// How many OTHER templates share this feature key. When there is no override, changing the
    /// feature ring moves all of them together — which is exactly the trap the operator hit, so
    /// the page states it.
    /// </summary>
    public int SharedWithCount { get; init; }

    /// <summary>
    /// §566 — the mail's REAL subject, tokens rendered as readable placeholders, for line 1 of the
    /// Settings row: <c>Subject: "Master Class cancelled: [Master Class Title]"</c>.
    /// </summary>
    /// <remarks>
    /// 🔒 READ FROM THE TEMPLATE, NEVER HAND-WRITTEN. Operator 2026-07-28: *"include both the
    /// internal jargon + subject so it is 100% clear to everyone !"* — and reading it IS the point:
    /// the page then cannot drift from the mail that actually goes out. Empty only when a template
    /// has no <c>Subject:</c> line, which a build-failing test prevents.
    /// </remarks>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// §705.3b — the per-ROLE rings set for this mail (empty when it only has an all-roles ring).
    /// </summary>
    /// <remarks>
    /// Kept as the RAW rows rather than pre-resolved per role, so the page can distinguish *"this role
    /// has its own ring"* from *"this role is following the all-roles ring"*. That distinction is the
    /// whole point: a row showing an inherited value as though it were set invites him to trust a
    /// control he never touched.
    /// </remarks>
    public IReadOnlyDictionary<ParticipantRole, Ring> RoleRings { get; init; }
        = new Dictionary<ParticipantRole, Ring>();
}

/// <summary>
/// §515 — reads and writes the per-template release ring (operator 2026-07-28: <i>"i would like to
/// configure indiidual ring per mail, like ring 2 for speakers welome and ring 1 for sponsors"</i>).
///
/// <para>Without this, a ring lived only on the FEATURE key, and template→feature is many-to-one —
/// all five persona welcomes plus the whole Master Class funnel share <c>welcome-email</c>. Setting
/// "the ring for this template" therefore moved every one of them at once.</para>
/// </summary>
public sealed class EmailTemplateRingService
{
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;

    // §566 — optional so every existing construction (tests, legacy) keeps compiling. When wired,
    // each row carries the mail's REAL subject line read from the template.
    private readonly EmailTemplateProvider? _templates;

    public EmailTemplateRingService(
        CommunityHubDbContext db, FeatureGateService gate, TimeProvider clock,
        EmailTemplateProvider? templates = null)
    {
        _db = db;
        _gate = gate;
        _clock = clock;
        _templates = templates;
    }

    /// <summary>
    /// §705.3b — the effective ring for ONE mail and ONE recipient ROLE:
    /// <c>(template, role) ?? (template, all-roles) ?? Ring0</c>.
    /// </summary>
    /// <param name="recipientRole">
    /// The role of the person being mailed. <c>null</c> when the send cannot identify a role (an
    /// attendee with no participant row, an ad-hoc address) — then only the all-roles row applies.
    /// </param>
    /// <remarks>
    /// 🔑 <b>The unit of a ring is (mail × role)</b> — operator 2026-07-29: *"1 mail type to a role = 1
    /// ring gate. that is it."* One template genuinely serves several roles, so a per-template ring
    /// could never mean "hold the sponsors but release the speakers".
    ///
    /// <para><b>Specific beats general, and that is the whole §515 fix:</b> a <c>(template, Speaker)</c>
    /// row governs speakers and leaves every other role on the all-roles row, so setting one role can
    /// never move the others.</para>
    ///
    /// <para>Ordering is done in memory over the ≤8 rows a template can have (one per role + the
    /// all-roles row) rather than as two queries — this runs on EVERY send.</para>
    /// </remarks>
    public async Task<Ring> GetEffectiveRingAsync(
        int eventId, string templateKey, CancellationToken ct = default,
        ParticipantRole? recipientRole = null)
    {
        var rows = await _db.EmailTemplateRings.AsNoTracking()
            .Where(r => r.EventId == eventId && r.TemplateKey == templateKey)
            .Select(r => new { r.Role, r.ReleasedToRing })
            .ToListAsync(ct);

        // The role-specific row wins; the all-roles row (Role == null) is the fallback.
        var match = rows.FirstOrDefault(r => recipientRole != null && r.Role == recipientRole)
                    ?? rows.FirstOrDefault(r => r.Role == null);

        if (match is not null) return match.ReleasedToRing;

        // 🔒 §705.2 — FAIL CLOSED. A REGISTERED mail with no ring of its own resolves to the INNERMOST
        // ring, never Broad.
        //
        // This replaces a fallback to the FEATURE's ring. Operator 2026-07-29: *"i want INDIVIDUAL
        // EMAIL RING GATES - one for each ! and remove features gates relevant for emails."* With the
        // feature ring going away there is nothing legitimate left to inherit, and the old default for
        // an unknown key was `Rings.Default` = **Broad** — so a mail that slipped through unregistered
        // would have been sent to EVERYONE. Welcome and Master Class mail is the worst possible place
        // for that (§699.2).
        //
        // ⚠️ SAFE BECAUSE THE STATE WAS MADE EXPLICIT FIRST, not because the change is small: every
        // non-exempt registered mail was given an explicit row in BOTH editions (32 each) before this
        // flipped, each seeded with the ring it already resolved to. So this path is currently
        // unreachable for every shipped mail — it is the guard for the NEXT mail someone adds.
        //
        // Ring-exempt mails never reach here at all: their send sets `EmailContext.RingExempt`, which
        // bypasses the gate entirely (§704.1c).
        if (EmailTemplateCatalog.Map.ContainsKey(templateKey))
        {
            return Ring.Ring0;
        }

        // NOT a registered mail — an ad-hoc template name. Unchanged behaviour: fall back to whatever
        // feature the send declared. Tightening this would silently drop mail that never had a mail-level
        // ring to begin with, which is a separate decision from §705.
        return await _gate.GetReleasedRingAsync(
            EmailTemplateCatalog.FeatureKeyFor(templateKey), eventId, ct);
    }

    /// <summary>Every catalog template with its effective ring, for the Settings page.</summary>
    public async Task<IReadOnlyList<EmailTemplateRingState>> GetAllAsync(
        int eventId, CancellationToken ct = default)
    {
        // 🔒 §705.3b — a template can now have SEVERAL rows (one per role + the all-roles row), so this
        // must NOT be a ToDictionary keyed on TemplateKey alone: that threw a duplicate-key exception
        // the moment the first per-role ring existed, which would have 500'd the whole Settings page.
        var allRows = await _db.EmailTemplateRings.AsNoTracking()
            .Where(r => r.EventId == eventId)
            .Select(r => new { r.TemplateKey, r.Role, r.ReleasedToRing })
            .ToListAsync(ct);

        // The all-roles ring (Role == null) — at most one per template, enforced by the unique index.
        var overrides = allRows
            .Where(r => r.Role == null)
            .ToDictionary(r => r.TemplateKey, r => r.ReleasedToRing, StringComparer.OrdinalIgnoreCase);

        // The per-role rings, grouped per template.
        var roleRings = allRows
            .Where(r => r.Role != null)
            .GroupBy(r => r.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<ParticipantRole, Ring>)g
                    .ToDictionary(r => r.Role!.Value, r => r.ReleasedToRing),
                StringComparer.OrdinalIgnoreCase);

        // One ring read per DISTINCT feature, not per template — 35 templates share far fewer keys.
        var featureKeys = EmailTemplateCatalog.Map.Values
            .Select(v => v.FeatureKey).Distinct(StringComparer.Ordinal).ToList();
        var featureRings = new Dictionary<string, Ring>(StringComparer.Ordinal);
        foreach (var fk in featureKeys)
            featureRings[fk] = await _gate.GetReleasedRingAsync(fk, eventId, ct);

        var sharedCounts = EmailTemplateCatalog.Map
            .GroupBy(kv => kv.Value.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return EmailTemplateCatalog.Map
            .Select(kv =>
            {
                var fk = kv.Value.FeatureKey;
                return new EmailTemplateRingState(
                    kv.Key,
                    kv.Value.Title,
                    EmailTemplateCatalog.AudienceFor(kv.Key),
                    fk,
                    featureRings.TryGetValue(fk, out var fr) ? fr : Ring.Broad,
                    overrides.TryGetValue(kv.Key, out var or) ? or : null)
                {
                    SharedWithCount = (sharedCounts.TryGetValue(fk, out var c) ? c : 1) - 1,
                    // §566 — line 1 of the row. Read from the template so it cannot drift from the
                    // mail that actually goes out; never throws, so a cosmetic gap can't 500 the page.
                    Subject = SubjectOrEmpty(kv.Key),
                    // §705.3b — the per-role rings, so the page can show which roles differ.
                    RoleRings = roleRings.TryGetValue(kv.Key, out var rr)
                        ? rr
                        : new Dictionary<ParticipantRole, Ring>(),
                };
            })
            .OrderBy(s => (int)s.Audience)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// §566 — the template's readable subject, or empty when no provider is wired / the file is
    /// unreadable. Deliberately swallows: the Settings page must render even if one template is
    /// missing, and the build-failing subject test is what guarantees they all have one.
    /// </summary>
    private string SubjectOrEmpty(string templateKey)
    {
        // §704.1b — a mail composed IN CODE has no template file to read a Subject: line from, so its
        // subject is declared in the catalog instead. Checked FIRST: it is authoritative for those
        // keys, and no file exists that could disagree with it.
        if (EmailTemplateCatalog.InlineSubjects.TryGetValue(templateKey, out var inline)) return inline;

        if (_templates is null) return string.Empty;
        try { return _templates.SubjectFor(templateKey); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Set this mail's ring, for one ROLE or for every role. Refuses a key that is not in the catalog,
    /// so a typo cannot create a row that silently governs nothing.
    /// </summary>
    /// <param name="role">
    /// §705.3b — the role this ring applies to, or <c>null</c> for "every role". Setting a role-specific
    /// ring leaves every other role on the all-roles row, which is the §515 trap solved: changing one
    /// role can never move the others.
    /// </param>
    public async Task<bool> SetRingAsync(
        int eventId, string templateKey, Ring ring, string? byEmail, CancellationToken ct = default,
        ParticipantRole? role = null)
    {
        if (!EmailTemplateCatalog.Map.ContainsKey(templateKey)) return false;

        // 🔒 Matched on ROLE too, or setting a per-role ring would overwrite the all-roles row instead
        // of adding beside it — silently changing every other role's audience.
        var row = await _db.EmailTemplateRings
            .FirstOrDefaultAsync(
                r => r.EventId == eventId && r.TemplateKey == templateKey && r.Role == role, ct);

        if (row is null)
        {
            row = new EmailTemplateRing { EventId = eventId, TemplateKey = templateKey, Role = role };
            _db.EmailTemplateRings.Add(row);
        }

        row.ReleasedToRing = ring;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Drop the override so the template follows its feature's ring again. Reversible by design —
    /// an operator must be able to undo a per-mail decision without a deploy.
    /// </summary>
    /// <param name="role">
    /// §705.3b — which row to clear: one role's, or the all-roles row (<c>null</c>). Clearing a role's
    /// ring drops that role back to the all-roles row, or to FAIL-CLOSED if there isn't one.
    /// </param>
    public async Task<bool> ClearRingAsync(
        int eventId, string templateKey, CancellationToken ct = default,
        ParticipantRole? role = null)
    {
        var row = await _db.EmailTemplateRings
            .FirstOrDefaultAsync(
                r => r.EventId == eventId && r.TemplateKey == templateKey && r.Role == role, ct);
        if (row is null) return false;

        _db.EmailTemplateRings.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
