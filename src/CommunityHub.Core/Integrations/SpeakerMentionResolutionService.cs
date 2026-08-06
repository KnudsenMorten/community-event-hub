using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What one resolution sweep did — enough for the job log to be honest about it.</summary>
public sealed record SpeakerMentionSweepResult(
    int Considered, int Resolved, int NotFollowers, int Ambiguous, int Failed, int Skipped)
{
    /// <summary>
    /// 🔒 §858.16h — a sweep that failed to ASK is not a sweep that found nothing. If lookups failed,
    /// the counts below are not a measurement of who follows the page, and the log must say so.
    /// </summary>
    public bool IsTrustworthy => Failed == 0;

    public override string ToString() =>
        $"considered {Considered}: resolved {Resolved}, not-followers {NotFollowers}, "
      + $"ambiguous {Ambiguous}, failed {Failed}, skipped {Skipped}"
      + (IsTrustworthy ? string.Empty : "  ⚠ lookups FAILED — counts are not a follower measurement");
}

/// <summary>
/// §858.16c / §884.1 — the SCHEDULED routine that resolves PEOPLE to mentionable LinkedIn person
/// URNs and caches them on <see cref="Participant.LinkedInPersonUrn"/>.
///
/// <para>🔒 <b>This is the ONLY place that calls LinkedIn for mentions.</b> Resolution must not happen
/// in the request path or at publish time: the endpoint carries a DAY throttle, and a member URN
/// never changes, so this is a cache-fill, not a live lookup (project guardrail: sync work flows
/// through the scheduled routines).</para>
///
/// <para>🔑 <b>It covers speakers AND sponsor contacts</b> (operator 2026-08-06: *"precheck also for
/// sponsors"*). Measured follower rates: speakers 16/22, sponsor signers 7/14, coordinators 6/15 —
/// so every cohort is worth resolving, and none of them is close to complete.</para>
///
/// <para>Re-runs are cheap and useful: an already-resolved person is skipped entirely, while the
/// unresolved are re-asked — someone who follows the page tomorrow becomes mentionable without
/// anyone doing anything.</para>
/// </summary>
public sealed class SpeakerMentionResolutionService
{
    private readonly CommunityHubDbContext _db;
    private readonly LinkedInPeopleTypeaheadClient _typeahead;
    private readonly TimeProvider _clock;
    private readonly ILogger<SpeakerMentionResolutionService>? _log;

    public SpeakerMentionResolutionService(
        CommunityHubDbContext db,
        LinkedInPeopleTypeaheadClient typeahead,
        TimeProvider clock,
        ILogger<SpeakerMentionResolutionService>? log = null)
    {
        _db = db;
        _typeahead = typeahead;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Resolve every active person we might want to tag who does not already have a URN — every
    /// speaker, plus every sponsor contact (signers and event coordinators included).
    /// <paramref name="reresolveAll"/> forces the resolved ones to be asked again (rare — only if a
    /// stored URN is suspected wrong; a URN itself never changes).
    /// </summary>
    /// <summary>
    /// §884.4 — how long a *negative* answer is trusted before the person is asked about again.
    /// <para>🔒 <b>This is what makes a 30-minute cadence safe.</b> The job runs every half hour so a
    /// NEW speaker or sponsor contact is tagged within the hour — but re-asking the ~30 people who do
    /// not follow the page 48 times a day would be ~1,500 calls against a DAY-limited endpoint, and
    /// the quota would be gone by morning. Every result would then be <c>LookupFailed</c>, which is
    /// the exact state that reads as "nobody follows the page" (§858.16h).</para>
    /// <para>So: someone already resolved is skipped forever (a URN never changes), and someone
    /// answered NEGATIVELY is re-asked once a day — enough to catch them following the page, cheap
    /// enough to leave the quota for the people we have never seen.</para>
    /// </summary>
    public static readonly TimeSpan NegativeAnswerTtl = TimeSpan.FromHours(24);

    public async Task<SpeakerMentionSweepResult> ResolveAsync(
        int eventId, string organizationUrnOrId, bool reresolveAll = false,
        CancellationToken ct = default)
    {
        // Everyone mentionable: speakers (they carry the profile URL that disambiguates) and
        // sponsor-role contacts (§884.3 — signer and coordinators are template variables).
        var people = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        // §888.3 — ORGANIZERS too: {Organizers} mentions them like any other
                        // variable now, so they need a cached URN like anyone else.
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.Sponsor
                            || p.Role == ParticipantRole.Organizer))
            .Select(p => new PersonToResolve(
                p.Id,
                p.FullName,
                p.LinkedInPersonUrn,
                p.LinkedInPersonUrnCheckedAt,
                _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == p.Id)
                    .Select(sp => sp.LinkedIn ?? sp.BackstageLinkedIn)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        int resolved = 0, notFollowers = 0, ambiguous = 0, failed = 0, skipped = 0;
        var now = _clock.GetUtcNow();

        foreach (var person in people)
        {
            if (!reresolveAll && ShouldSkip(person, now))
            {
                skipped++;
                continue;
            }

            var (first, last) = SplitName(person.FullName);
            var outcome = await _typeahead.ResolveAsync(
                organizationUrnOrId, first, last, person.ProfileUrl, ct);

            var row = await _db.Participants.FirstAsync(p => p.Id == person.ParticipantId, ct);
            row.LinkedInPersonUrnStatus = outcome.Status.ToString();
            row.LinkedInPersonUrnCheckedAt = _clock.GetUtcNow();

            switch (outcome.Status)
            {
                case MentionResolutionStatus.Resolved:
                    row.LinkedInPersonUrn = outcome.Urn;
                    row.LinkedInVanityName ??= PersonMentionMatcher.TryReadPersonSlug(person.ProfileUrl);
                    resolved++;
                    break;

                case MentionResolutionStatus.NotAFollower:
                    notFollowers++;
                    break;

                case MentionResolutionStatus.Ambiguous:
                    ambiguous++;
                    // 🔒 Named in the log: he can settle an ambiguity by hand, but only if he knows.
                    _log?.LogWarning(
                        "Person mention AMBIGUOUS for {Name}: {Reason}", person.FullName, outcome.Reason);
                    break;

                default:
                    failed++;
                    // 🔒 Do NOT let a failure look like a negative — leave any existing URN alone.
                    _log?.LogWarning(
                        "Person mention lookup FAILED for {Name}: {Reason}", person.FullName, outcome.Reason);
                    break;
            }
        }

        await _db.SaveChangesAsync(ct);

        var result = new SpeakerMentionSweepResult(
            people.Count, resolved, notFollowers, ambiguous, failed, skipped);

        _log?.LogInformation("Person mention resolution — {Result}", result);
        return result;
    }

    private sealed record PersonToResolve(
        int ParticipantId, string FullName, string? ExistingUrn,
        DateTimeOffset? CheckedAt, string? ProfileUrl);

    /// <summary>
    /// §884.4 — skip anyone we already know, and anyone we asked about recently.
    /// <list type="bullet">
    ///   <item><b>Has a URN ⇒ skip forever.</b> A member URN never changes.</item>
    ///   <item><b>Answered negatively within <see cref="NegativeAnswerTtl"/> ⇒ skip for now</b>, so a
    ///     half-hourly job does not spend the day's quota re-asking the same non-followers.</item>
    ///   <item><b>Never checked ⇒ always ask.</b> This is the case the 30-minute cadence exists for:
    ///     a speaker or sponsor contact added an hour ago is tagged in the next post.</item>
    /// </list>
    /// </summary>
    internal static bool ShouldSkip(
        int participantId, string? existingUrn, DateTimeOffset? checkedAt, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(existingUrn)) return true;
        if (checkedAt is null) return false;
        return now - checkedAt.Value < NegativeAnswerTtl;
    }

    private static bool ShouldSkip(PersonToResolve p, DateTimeOffset now) =>
        ShouldSkip(p.ParticipantId, p.ExistingUrn, p.CheckedAt, now);

    /// <summary>
    /// Split a full name so the SURNAME lands last — that is what the search keyword is built from,
    /// and a surname is far more selective than a given name (§858.16h).
    /// </summary>
    internal static (string? First, string? Last) SplitName(string? fullName)
    {
        var full = (fullName ?? string.Empty).Trim();
        if (full.Length == 0) return (null, null);

        var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? (parts[0], null)
            : (string.Join(' ', parts[..^1]), parts[^1]);
    }
}
