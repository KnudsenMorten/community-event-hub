using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §447 (operator 2026-07-27) — he renamed the Zoho ticket classes to
/// <c>"2-day (Pre-day + Main Event)"</c> and <c>"1-day (Main Event)"</c>, because buyers were
/// reading the 1-day ticket as including the pre-day, and asked that BOTH the old and new naming
/// keep working since older orders carry the old text.
///
/// <para>This is the highest-consequence string in the system: it decides who is a 2-day holder,
/// which drives hub sign-in (<c>OneDayAccessGate</c>), Master Class eligibility, the party task and
/// the Zoho chasers. Getting it wrong locks real attendees out or lets 1-day holders in.</para>
///
/// <para>Two mechanisms are pinned here: the NAME rule (a substring match, so parentheses are
/// harmless) and the new ID rule (Zoho's stable <c>ticket_class_id</c>, which a rename cannot
/// touch). The real ids are the operator's own, cross-checked against a mirrored PROD order.</para>
/// </summary>
public sealed class TicketClassRenameTests
{
    // Confirmed twice on 2026-07-27: the operator's buyTickets?ticketClassId= links, and a real
    // PROD order carrying ticket_class_id 14880000003485482 next to the 2-day ticket_name.
    private const string TwoDayClassId = "14880000003485482";
    private const string OneDayClassId = "14880000003485481";

    private static readonly string[] ConfiguredTwoDayIds = { TwoDayClassId };

    // ---- the NAME rule: old and new must both resolve ----------------------

    [Theory]
    // NEW naming (§447) — the parentheses must not break the match.
    [InlineData("2-day (Pre-day + Main Event)", true)]
    // The exact string Zoho actually returned on PROD, double space and all.
    [InlineData("2-day (Pre-day  + Main Event)", true)]
    // OLD naming — historic orders still carry this.
    [InlineData("2-day Pre-day + Main Event", true)]
    // NEW 1-day naming — must NOT grant Master Class access.
    [InlineData("1-day (Main Event)", false)]
    // OLD 1-day naming.
    [InlineData("1-day Main Event", false)]
    // Case and spacing variants of the marker itself.
    [InlineData("2 Day (Pre-day + Main Event)", true)]
    [InlineData("2-DAY (PRE-DAY + MAIN EVENT)", true)]
    // Neither.
    [InlineData("Test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_name_rule_accepts_both_the_old_and_the_new_naming(string? className, bool expected)
    {
        Assert.Equal(expected, MasterClassTicketPolicy.IncludesMasterClass(className));
    }

    // ---- the ID rule: authoritative, and rename-proof -----------------------

    [Fact]
    public void The_configured_two_day_class_id_grants_access()
    {
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            TwoDayClassId, "2-day (Pre-day + Main Event)", ConfiguredTwoDayIds));
    }

    [Fact]
    public void The_one_day_class_id_does_not()
    {
        Assert.False(MasterClassTicketPolicy.IncludesMasterClass(
            OneDayClassId, "1-day (Main Event)", ConfiguredTwoDayIds));
    }

    /// <summary>
    /// THE point of using an id. If the operator renames the 2-day class to something with no
    /// "2-day" in it at all, the id must still grant access — otherwise the rename silently locks
    /// out every 2-day holder.
    /// </summary>
    [Fact]
    public void A_future_rename_cannot_break_a_two_day_holder()
    {
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            TwoDayClassId, "Full Conference Pass incl. Workshops", ConfiguredTwoDayIds));
    }

    /// <summary>
    /// And the converse: a name that LOOKS two-day must not grant access when the id says
    /// otherwise. The id is authoritative in both directions or it is not authoritative at all.
    /// </summary>
    [Fact]
    public void A_misleading_name_cannot_override_the_id()
    {
        Assert.False(MasterClassTicketPolicy.IncludesMasterClass(
            OneDayClassId, "2-day (mislabelled)", ConfiguredTwoDayIds));
    }

    // ---- the fallback: historic orders keep working -------------------------

    /// <summary>
    /// A ticket with NO class id (an older payload) must still resolve by name, even when ids are
    /// configured — this is what "support both the old and new naming" actually requires.
    /// </summary>
    [Fact]
    public void A_ticket_without_a_class_id_still_resolves_by_name()
    {
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            null, "2-day Pre-day + Main Event", ConfiguredTwoDayIds));
        Assert.False(MasterClassTicketPolicy.IncludesMasterClass(
            null, "1-day (Main Event)", ConfiguredTwoDayIds));
    }

    /// <summary>With no ids configured at all, behaviour is exactly the pre-§447 name rule.</summary>
    [Fact]
    public void With_no_ids_configured_the_name_rule_decides()
    {
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            OneDayClassId, "2-day (Pre-day + Main Event)", twoDayClassIds: null));
        Assert.True(MasterClassTicketPolicy.IncludesMasterClass(
            TwoDayClassId, "2-day (Pre-day + Main Event)", Array.Empty<string>()));
    }

    // ---- end to end through the sync mapper --------------------------------

    private static BackstageAttendee Ticket(string className, string? classId, bool attending = true) =>
        new(TicketId: "t1", OrderId: "o1", Email: "a@example.test", FirstName: "A", LastName: "B",
            TicketClassName: className, Attending: attending,
            CompanyName: null, JobTitle: null, Phone: null, Country: null, CountryCode: null,
            City: null, Postcode: null, TaxId: null, CustomFieldsJson: null,
            CreatedTimeRaw: null, StatusString: "attending", TicketClassId: classId);

    [Fact]
    public void The_sync_mapper_classifies_the_renamed_classes_correctly()
    {
        var twoDay = AttendeeTicketSyncService.FromBackstage(
            Ticket("2-day (Pre-day + Main Event)", TwoDayClassId), ConfiguredTwoDayIds);
        Assert.Equal(TicketStatus.TwoDay, twoDay.Status);

        var oneDay = AttendeeTicketSyncService.FromBackstage(
            Ticket("1-day (Main Event)", OneDayClassId), ConfiguredTwoDayIds);
        Assert.Equal(TicketStatus.Other, oneDay.Status);

        // A historic order: old name, no class id.
        var historic = AttendeeTicketSyncService.FromBackstage(
            Ticket("2-day Pre-day + Main Event", null), ConfiguredTwoDayIds);
        Assert.Equal(TicketStatus.TwoDay, historic.Status);
    }

    /// <summary>
    /// A not-attending ticket is <see cref="TicketStatus.None"/> regardless of class — the id must
    /// not promote a cancelled ticket to 2-day.
    /// </summary>
    [Fact]
    public void A_not_attending_ticket_is_none_whatever_its_class()
    {
        var row = AttendeeTicketSyncService.FromBackstage(
            Ticket("2-day (Pre-day + Main Event)", TwoDayClassId, attending: false),
            ConfiguredTwoDayIds);
        Assert.Equal(TicketStatus.None, row.Status);
    }

    /// <summary>
    /// The policy is only rename-proof if the edition actually CONFIGURES the id. A correct rule
    /// with an empty config silently degrades to name-matching, which is the very thing the id is
    /// there to survive — so pin that the shipped edition config carries it.
    /// </summary>
    [PrivateContentFact] // pins the upstream edition's own config values
    public void The_shipped_edition_config_wires_the_two_day_class_id()
    {
        var path = Path.Combine(
            CommunityHub.Core.Tests.Scenario.RepoPaths.RepoRoot(), "config", "event.eldk27.json");
        Assert.True(File.Exists(path), $"edition config not found at {path}");

        var cfg = new CommunityHub.Core.Config.EventEditionConfigLoader().Load(path);

        Assert.Contains(TwoDayClassId, cfg.MasterClassTwoDayClassIds);
        // The 1-day class must NOT be listed — anything unlisted is treated as non-2-day.
        Assert.DoesNotContain(OneDayClassId, cfg.MasterClassTwoDayClassIds);
        // And the name fallback survives for historic orders.
        Assert.Contains("2-day", cfg.MasterClassNameMarkers);
    }
}
