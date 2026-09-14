using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1175 — say it once when a write keeps not landing, and keep sending it anyway.
///
/// <para>Operator 2026-09-03, on the same sponsors in the ops mail run after run: <i>"but the
/// reconsile must verify the existing value and only change it different"</i>. It already did — the
/// comparison is correct and the read/write keys match. The fault was one level up: CEH ANNOUNCED
/// the same write for ever without noticing it was the same write.</para>
///
/// <para>🔴 The first draft of this ledger capped the PUSH at three attempts, which is precisely the
/// <c>ZohoSocialPushedHash</c> that §784.13 removed — it cost the operator ten of thirteen sponsors'
/// LinkedIn URLs, and it would have made §1087 (Zoho repairing the endpoint on 2026-08-16) heal
/// nothing. The tests below pin the corrected split: <b>the mail is gated, the push never is.</b></para>
/// </summary>
public sealed class ZohoPushLedgerTests
{
    private static SponsorInfo Company() => new() { SponsorCompanyId = "42", CompanyName = "Contoso" };

    private const string Desc = "sponsor:description";

    /// <summary>Send it <paramref name="times"/> times and give back the last verdict.</summary>
    private static ZohoPushReport SendRepeatedly(SponsorInfo info, string value, int times)
    {
        var report = ZohoPushReport.Normal;
        for (var i = 0; i < times; i++)
            report = ZohoPushLedger.ReportFor(ZohoPushLedger.RecordSent(info, Desc, value));
        return report;
    }

    /// <summary>
    /// 🔴 THE GUARDRAIL. Nothing this class exposes can ever say "do not send it".
    /// </summary>
    /// <remarks>
    /// §784.13 is explicit — <i>"Do NOT reintroduce a 'push once' memo to reduce chatter"</i>. The
    /// re-push is idempotent and fires only while Zoho is actually blank, so it self-heals the
    /// moment Zoho starts accepting the field. A memo that stops it turns a transient Zoho fault
    /// into permanent data loss, silently.
    /// </remarks>
    [Fact]
    public void The_ledger_never_offers_a_way_to_stop_pushing()
    {
        var members = typeof(ZohoPushLedger).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Push", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Skip", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Suppress", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(members);
    }

    [Fact]
    public void A_first_send_is_announced_normally()
    {
        Assert.Equal(ZohoPushReport.Normal, SendRepeatedly(Company(), "We make widgets.", 1));
    }

    /// <summary>
    /// 🔑 The early sends are still announced — three, not one.
    /// </summary>
    /// <remarks>
    /// A single unseen write is ordinary: Zoho may be eventually consistent, and a draft awaiting
    /// publication resolves by itself. Warning at one would flag every ordinary push as trouble.
    /// </remarks>
    [Fact]
    public void It_warns_once_at_the_threshold_then_goes_quiet()
    {
        var info = Company();
        const string value = "We make widgets.";

        for (var i = 1; i < ZohoPushLedger.ReportAfterAttempts; i++)
            Assert.Equal(ZohoPushReport.Normal,
                ZohoPushLedger.ReportFor(ZohoPushLedger.RecordSent(info, Desc, value)));

        // The run that crosses the line gets the one warning…
        Assert.Equal(ZohoPushReport.Warn,
            ZohoPushLedger.ReportFor(ZohoPushLedger.RecordSent(info, Desc, value)));

        // …and every run after it is silent, for ever.
        Assert.Equal(ZohoPushReport.Silent,
            ZohoPushLedger.ReportFor(ZohoPushLedger.RecordSent(info, Desc, value)));
        Assert.Equal(ZohoPushReport.Silent, SendRepeatedly(info, value, 20));

        Assert.Contains(ZohoPushLedger.Stuck(info), e => e.Field == Desc);
    }

    /// <summary>
    /// 🔒 A CHANGED value is announced again — it is a new fact, not a repeat.
    /// </summary>
    /// <remarks>
    /// Otherwise a sponsor who edits their description after a stuck field would never see the edit
    /// mentioned, and the operator would have no way of knowing the new text was in flight.
    /// </remarks>
    [Fact]
    public void Changing_the_value_earns_a_fresh_announcement()
    {
        var info = Company();
        Assert.Equal(ZohoPushReport.Silent, SendRepeatedly(info, "Old text.", 10));

        Assert.Equal(ZohoPushReport.Normal,
            ZohoPushLedger.ReportFor(ZohoPushLedger.RecordSent(info, Desc, "New text.")));
    }

    /// <summary>
    /// 🔒 Arriving clears the entry, so a healthy field leaves no trace and a relapse is reported afresh.
    /// </summary>
    [Fact]
    public void Clearing_forgets_the_field_entirely()
    {
        var info = Company();
        Assert.Equal(ZohoPushReport.Silent, SendRepeatedly(info, "text", 10));

        Assert.True(ZohoPushLedger.Clear(info, Desc));

        Assert.Empty(ZohoPushLedger.Stuck(info));
        Assert.Null(info.ZohoUnconfirmedPushesJson);
        Assert.Equal(ZohoPushReport.Normal, SendRepeatedly(info, "text", 1));
    }

    [Fact]
    public void Clearing_something_that_was_never_stuck_changes_nothing()
    {
        var info = Company();
        Assert.False(ZohoPushLedger.Clear(info, Desc));
        Assert.Null(info.ZohoUnconfirmedPushesJson);
    }

    /// <summary>
    /// 🔴 The sponsor and the exhibitor record share one row — and both have a website.
    /// </summary>
    /// <remarks>
    /// Unprefixed keys would let the exhibitor's website arriving clear the sponsor's entry, so the
    /// two records would silence each other and a genuinely stuck field would never be reported.
    /// </remarks>
    [Fact]
    public void The_two_records_cannot_silence_each_other()
    {
        var info = Company();
        var sponsorWebsite = ZohoPushLedger.SponsorKey("website_url");
        var exhibitorWebsite = ZohoPushLedger.ExhibitorKey("website_url");

        Assert.NotEqual(sponsorWebsite, exhibitorWebsite);

        for (var i = 0; i < ZohoPushLedger.ReportAfterAttempts; i++)
            ZohoPushLedger.RecordSent(info, sponsorWebsite, "https://contoso.example");

        // The exhibitor's copy arrives; the sponsor's is still outstanding.
        ZohoPushLedger.Clear(info, exhibitorWebsite);

        Assert.Contains(ZohoPushLedger.Stuck(info), e => e.Field == sponsorWebsite);
    }

    /// <summary>
    /// 🔒 Fields are tracked apart — a stuck description must not mute the website.
    /// </summary>
    [Fact]
    public void One_stuck_field_does_not_mute_another()
    {
        var info = Company();
        Assert.Equal(ZohoPushReport.Silent, SendRepeatedly(info, "text", 10));

        Assert.Equal(ZohoPushReport.Normal, ZohoPushLedger.ReportFor(
            ZohoPushLedger.RecordSent(info, ZohoPushLedger.SponsorKey("website_url"),
                "https://contoso.example")));
    }

    /// <summary>
    /// 🔒 Unreadable JSON is "nothing remembered", never an exception.
    /// </summary>
    /// <remarks>
    /// §1140b: "I cannot read it" is not a fact about the value. The cost of forgetting is one extra
    /// announcement; the cost of throwing is the whole reconcile stopping for every company over one
    /// malformed row.
    /// </remarks>
    [Fact]
    public void Malformed_json_is_empty_not_a_throw()
    {
        var info = Company();
        info.ZohoUnconfirmedPushesJson = "{ not json";

        Assert.Empty(ZohoPushLedger.Read(info));
        Assert.Equal(ZohoPushReport.Normal, SendRepeatedly(info, "text", 1));
    }

    [Fact]
    public void The_stored_form_never_contains_the_value_itself()
    {
        // 🔒 A description is long and is the sponsor's own copy; the ledger only needs identity.
        var info = Company();
        ZohoPushLedger.RecordSent(info, Desc, "A very specific sentence about widgets.");

        Assert.DoesNotContain("widgets", info.ZohoUnconfirmedPushesJson);
    }

    [Fact]
    public void The_hash_is_stable_and_ignores_surrounding_space()
    {
        Assert.Equal(ZohoPushLedger.HashOf("text"), ZohoPushLedger.HashOf("  text  "));
        Assert.NotEqual(ZohoPushLedger.HashOf("text"), ZohoPushLedger.HashOf("other"));
    }
}
