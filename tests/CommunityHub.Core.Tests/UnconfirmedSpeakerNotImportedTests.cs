using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1243 — a speaker whose sessions are ALL unconfirmed is not imported either.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, on finding four such people sitting in the Pending-speakers queue:
/// *"some of these names have NOT yet approved their participation (the gate we added earlier). but
/// they are already in the pending queue, which is NOT good"* · *"importer should NOT import
/// sessions or speakers that have not approved yet"*.</para>
///
/// <para>🔑 <b>Why it matters more than queue tidiness.</b> Approving someone in that queue sets
/// their category, and a categorized+active speaker is exactly what the Backstage push may attach to
/// a session — which makes Zoho create the speaker and SEND THEM AN INVITATION (§326bx, 19 people
/// invited prematurely). So the queue was inviting him to invite four people who had not agreed to
/// speak.</para>
///
/// <para>⚠️ Sessionize has <b>no speaker-level confirmation flag</b> — verified against the live v2
/// payload, it exists only per SESSION. So the rule is derived, and these tests pin the derivation:
/// hold a speaker only when EVERY session they are on is unconfirmed.</para>
/// </remarks>
public sealed class UnconfirmedSpeakerNotImportedTests
{
    /// <summary>
    /// The speakers that WOULD be imported — i.e. the real
    /// <see cref="CommunityHub.Core.Reminders.SessionizeApiImportService.UnconfirmedSpeakers"/>
    /// subtracted from the full list. 🔒 Calls the production rule rather than restating it: a
    /// test that reimplements the logic passes even when the logic is wrong.
    /// </summary>
    private static IReadOnlyList<SessionizeSpeaker> ApplyGate(
        IReadOnlyList<SessionizeSpeaker> speakers,
        IReadOnlyList<SessionizeSession> sessions)
    {
        var held = CommunityHub.Core.Reminders.SessionizeApiImportService
            .UnconfirmedSpeakers(speakers, sessions)
            .Select(s => s.SessionizeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return speakers.Where(s => !held.Contains(s.SessionizeId)).ToList();
    }

    private static SessionizeSpeaker Spk(string id) =>
        new($"{id}@x.invalid", id, "Person", null, SessionizeId: id);

    private static SessionizeSession Sess(string id, bool confirmed, params string[] speakerIds) =>
        new(id, "S" + id, null, null, "Security", null, null,
            IsServiceSession: false, SpeakerIds: speakerIds, IsConfirmed: confirmed);

    [Fact]
    public void A_speaker_with_no_confirmed_session_is_held()
    {
        // The real case: Rasmus Hald — one session, not confirmed, yet in the queue.
        var kept = ApplyGate(
            new[] { Spk("rasmus") },
            new[] { Sess("s1", confirmed: false, "rasmus") });

        Assert.Empty(kept);
    }

    [Fact]
    public void One_confirmed_session_is_enough_to_let_a_speaker_through()
    {
        // 🔑 The real case this protects: Wim Matthyssen and Pim Jacobs each have two sessions,
        // one confirmed and one not. They HAVE committed to the event, so holding them because a
        // second talk is still open would be wrong — and would look identical to the bug.
        var kept = ApplyGate(
            new[] { Spk("wim") },
            new[]
            {
                Sess("s1", confirmed: false, "wim"),
                Sess("s2", confirmed: true, "wim"),
            });

        Assert.Equal("wim", Assert.Single(kept).SessionizeId);
    }

    [Fact]
    public void A_speaker_on_no_session_at_all_is_NOT_held()
    {
        // ⚠️ They have nothing to confirm. Treating "never asked" as "declined" would drop somebody
        // for a state they were never in.
        var kept = ApplyGate(
            new[] { Spk("orphan") },
            new[] { Sess("s1", confirmed: true, "someone-else") });

        Assert.Equal("orphan", Assert.Single(kept).SessionizeId);
    }

    [Fact]
    public void An_EMPTY_session_list_imports_EVERY_speaker()
    {
        // 🔴 THE FAIL-OPEN GUARD, and the most important test here. A failed or empty session view
        // makes every speaker look unconfirmed. Without this the whole edition would silently stop
        // importing speakers — no error anywhere, just nobody arriving. Same shape as §326ax (an
        // empty join is not evidence) and §1241 (an absent signal is not a negative one).
        var speakers = new[] { Spk("a"), Spk("b"), Spk("c") };

        var kept = ApplyGate(speakers, Array.Empty<SessionizeSession>());

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void The_live_shape_holds_the_four_and_keeps_the_two()
    {
        // The exact ELDK27 situation on 2026-09-20: six speakers on unconfirmed sessions, but two
        // of them have a second, confirmed session.
        var speakers = new[]
        {
            Spk("jeroen"), Spk("rasmus"), Spk("michael"), Spk("sebastian"),  // 1 session, unconfirmed
            Spk("pim"), Spk("wim"),                                          // 2 sessions, 1 confirmed
            Spk("saurabh"),                                                  // confirmed
        };
        var sessions = new[]
        {
            Sess("u1", false, "jeroen"), Sess("u2", false, "rasmus"),
            Sess("u3", false, "michael"), Sess("u4", false, "sebastian"),
            Sess("u5", false, "pim"),     Sess("c5", true,  "pim"),
            Sess("u6", false, "wim"),     Sess("c6", true,  "wim"),
            Sess("c7", true,  "saurabh"),
        };

        var kept = ApplyGate(speakers, sessions).Select(s => s.SessionizeId).ToList();

        Assert.Equal(new[] { "pim", "wim", "saurabh" }.Order(), kept.Order());
    }
}
