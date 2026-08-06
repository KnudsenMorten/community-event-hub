namespace CommunityHub.Core.Integrations;

/// <summary>One person to be named in a post, as CEH knows them.</summary>
public sealed record MentionablePerson(
    string? FirstName,
    string? LastName,
    string? ProfileUrl)
{
    /// <summary>The organizer's own copy of the name — what a reader sees when we cannot mention.</summary>
    public string FullName => $"{FirstName} {LastName}".Replace("  ", " ").Trim();
}

/// <summary>
/// §858.16c — one person's outcome, carrying everything the organizer needs to act.
/// 🔒 <see cref="ProfileUrl"/> is on here deliberately: when a mention is impossible he has to open
/// the profile and tag by hand, and a name alone is not enough to find the right person
/// (operator 2026-08-06: *"for non-followers i need their full name so I can tag them manually"*).
/// </summary>
public sealed record MentionOutcome(
    MentionablePerson Person,
    MentionResolution Resolution)
{
    public bool Mentioned => Resolution.IsResolved;

    /// <summary>What goes into the post for this person: a real mention, or their plain full name.</summary>
    public string Rendered => Mentioned
        ? PersonMentionMatcher.RenderMention(Person.FullName, Resolution.Urn!)
        : Person.FullName;
}

/// <summary>
/// §858.16 — the composed result: the text to publish, and an HONEST account of who was not mentioned.
/// </summary>
public sealed record MentionComposition(
    string Text,
    IReadOnlyList<MentionOutcome> Outcomes)
{
    public IReadOnlyList<MentionOutcome> Mentioned =>
        Outcomes.Where(o => o.Mentioned).ToList();

    /// <summary>Everyone who fell back to a plain name — the manual-tagging worklist.</summary>
    public IReadOnlyList<MentionOutcome> Fallbacks =>
        Outcomes.Where(o => !o.Mentioned).ToList();

    /// <summary>
    /// 🔒 True when the lookup itself failed for someone. That is NOT the same as a non-follower and
    /// must be surfaced differently — retrying later may mention them (§858.16h).
    /// </summary>
    public bool HadLookupFailures =>
        Outcomes.Any(o => o.Resolution.Status == MentionResolutionStatus.LookupFailed);

    /// <summary>
    /// The organizer-facing note. §854: an unreported fallback is the defect — he must be told WHO
    /// to tag by hand, WHY, and WHERE their profile is, or the post goes out quietly under-credited.
    /// Returns null when everyone was mentioned and there is nothing to chase.
    /// </summary>
    public string? FallbackReport()
    {
        if (Fallbacks.Count == 0) return null;

        var lines = new List<string>
        {
            $"{Fallbacks.Count} of {Outcomes.Count} could NOT be mentioned and appear as plain text. "
            + "Tag them by hand after publishing:",
        };

        foreach (var f in Fallbacks)
        {
            var where = string.IsNullOrWhiteSpace(f.Person.ProfileUrl)
                ? "no LinkedIn URL on file"
                : f.Person.ProfileUrl!;
            lines.Add($"  • {f.Person.FullName} — {f.Resolution.Reason} — {where}");
        }

        if (HadLookupFailures)
        {
            lines.Add(
                "  ⚠ At least one entry FAILED TO LOOK UP rather than being a confirmed non-follower — "
                + "retrying later may still mention them.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// §858.16c — composes the people half of a post: mention whoever follows the page, name everyone
/// else in plain text, and report the difference.
///
/// <para>🔑 The fallback is not a degradation we chose — LinkedIn enforces it. A non-follower cannot
/// be mentioned even with a valid URN (§858.16d), so ~1 in 4 speakers will always land here.</para>
/// </summary>
public sealed class SpeakerMentionComposer
{
    private readonly LinkedInPeopleTypeaheadClient _typeahead;

    public SpeakerMentionComposer(LinkedInPeopleTypeaheadClient typeahead) => _typeahead = typeahead;

    /// <summary>
    /// Resolve each person and build the name list (", " separated, "and" before the last).
    /// 🔒 §858.9: call this at PUBLISH time, never at plan time — a session's line-up changes between
    /// the two, and a mention resolved months early announces the line-up as it WAS.
    /// </summary>
    public async Task<MentionComposition> ComposeAsync(
        string organizationUrnOrId,
        IReadOnlyList<MentionablePerson> people,
        CancellationToken ct = default)
    {
        var outcomes = new List<MentionOutcome>(people.Count);

        foreach (var person in people)
        {
            var resolution = await _typeahead.ResolveAsync(
                organizationUrnOrId, person.FirstName, person.LastName, person.ProfileUrl, ct);
            outcomes.Add(new MentionOutcome(person, resolution));
        }

        return new MentionComposition(JoinNames(outcomes.Select(o => o.Rendered)), outcomes);
    }

    /// <summary>"A", "A and B", "A, B and C" — the mention markup rides along untouched.</summary>
    internal static string JoinNames(IEnumerable<string> parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
