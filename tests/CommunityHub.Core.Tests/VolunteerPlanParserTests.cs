using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §327k — the volunteer-plan CSV parser must resolve columns by HEADER NAME.
///
/// <para>It used to be purely positional, written for the ELDK26 export which carried a
/// <c>Status</c> column. The ELDK27 export dropped that column, so every field after it
/// shifted by one and the parser read the <i>ELDK Lead</i> column as the Responsible Team.
/// It did not throw — it reported buckets named after PEOPLE and would have imported 127
/// tasks into them. These tests pin both layouts. FAKE names only.</para>
/// </summary>
public sealed class VolunteerPlanParserTests
{
    private const string HeaderWithStatus =
        "T-day;Date;Time Start;Time End;Task Name;Status;Criticality;Responsible Team;" +
        "ELDK Lead Task;Resources Needed;Resource Names;Pre-req;Expectations";

    // The ELDK27 shape: NO Status column.
    private const string HeaderNoStatus =
        "T-day;Date;Time Start;Time End;Task Name;Criticality;Responsible Team;" +
        "ELDK Lead Task;Resources Needed;Resource Names;Pre-req;Expectations";

    [Fact]
    public void The_legacy_layout_with_a_Status_column_still_parses()
    {
        var csv = HeaderWithStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;Pack bags;Open;Need-to-have;ELDK-Volunteers;"
            + "Alex Lead;3;;prereq text;expect text";

        var plan = new VolunteerPlanParser().Parse(csv);

        var t = Assert.Single(plan.Tasks);
        Assert.Equal("Pack bags", t.Title);
        Assert.Equal("ELDK-Volunteers", t.ResponsibleTeam);      // NOT the lead's name
        Assert.Equal("Alex Lead", t.EldkLeadName);
        Assert.Equal(3, t.ResourcesNeeded);
        Assert.Equal(VolunteerTaskCriticality.NeedToHave, t.Criticality);
        Assert.Equal("prereq text", t.Prerequisites);
        Assert.Equal("expect text", t.Expectations);
    }

    [Fact]
    public void The_ELDK27_layout_without_Status_maps_every_column_correctly()
    {
        // THE REGRESSION: positionally, "ELDK-Volunteers" sits where the old layout expected
        // Criticality, and "Alex Lead" where it expected the team. Header-driven mapping is
        // the only thing that gets this right.
        var csv = HeaderNoStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;Pack bags;Need-to-have;ELDK-Volunteers;"
            + "Alex Lead;3;;prereq text;expect text";

        var plan = new VolunteerPlanParser().Parse(csv);

        var t = Assert.Single(plan.Tasks);
        Assert.Equal("Pack bags", t.Title);
        Assert.Equal("ELDK-Volunteers", t.ResponsibleTeam);
        Assert.Equal("Alex Lead", t.EldkLeadName);
        Assert.Equal(3, t.ResourcesNeeded);
        Assert.Equal(VolunteerTaskCriticality.NeedToHave, t.Criticality);
        Assert.Equal("prereq text", t.Prerequisites);
        Assert.Equal("expect text", t.Expectations);
    }

    [Fact]
    public void A_bucket_is_never_named_after_a_person()
    {
        // The symptom that exposed the bug: buckets called "Kent Agerlund" / "Martin Byskov".
        var csv = HeaderNoStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;Task A;Need-to-have;ELDK-Volunteers;Alex Lead;2;;;\n"
            + "T-0;08-02-2027;10:00;11:00;Task B;Nice-to-have;ELDK-Volunteers;Robin Lead;1;;;";

        var plan = new VolunteerPlanParser().Parse(csv);

        Assert.Equal(2, plan.Tasks.Count);
        var bucket = Assert.Single(plan.Buckets);
        Assert.Equal("ELDK-Volunteers", bucket);
        Assert.DoesNotContain(plan.Buckets, b => b.Contains("Lead", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_Status_column_every_task_is_Open()
    {
        var csv = HeaderNoStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;Task A;Need-to-have;ELDK-Volunteers;Alex Lead;2;;;";

        var plan = new VolunteerPlanParser().Parse(csv);

        // Nothing can have been completed in a file that does not track completion.
        Assert.Equal(VolunteerTaskStatus.Open, Assert.Single(plan.Tasks).Status);
    }

    [Fact]
    public void A_blank_team_falls_into_the_Unassigned_bucket()
    {
        var csv = HeaderNoStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;Orphan task;Need-to-have;;;1;;;";

        var plan = new VolunteerPlanParser().Parse(csv);

        Assert.Equal(VolunteerPlanParser.UnassignedBucket, Assert.Single(plan.Tasks).BucketName);
    }

    [Fact]
    public void Quoted_fields_containing_the_delimiter_survive()
    {
        var csv = HeaderNoStatus + "\n"
            + "T-1;07-02-2027;09:00;12:00;\"Mount walls; then signage\";Need-to-have;"
            + "ELDK-Volunteers;Alex Lead;4;;;";

        var plan = new VolunteerPlanParser().Parse(csv);

        var t = Assert.Single(plan.Tasks);
        Assert.Equal("Mount walls; then signage", t.Title);
        Assert.Equal("ELDK-Volunteers", t.ResponsibleTeam);   // the ';' did not shift columns
    }
}
