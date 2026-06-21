using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the no-API-key sponsor lead contact links (REQUIREMENTS §20
/// Participant "Leads no-API-key contact link"). The builder is pure, so these
/// pin the mailto:/tel: shaping, the display-name fallback, the contactable gate
/// and the header-injection safety.
/// </summary>
public sealed class SponsorLeadContactLinkTests
{
    private static SponsorLead Lead(
        string? full = null, string? first = null, string? last = null,
        string? email = null, string? phone = null, string? company = null) => new()
    {
        Id = 42, EventId = 1, SponsorCompanyId = "c1",
        FullName = full ?? string.Empty, FirstName = first ?? string.Empty,
        LastName = last ?? string.Empty, Email = email ?? string.Empty,
        Phone = phone ?? string.Empty, Company = company ?? string.Empty,
    };

    [Fact]
    public void A_lead_with_email_and_phone_gets_both_links()
    {
        var c = SponsorLeadContactLinkBuilder.Build(
            Lead(full: "Jane Doe", email: "jane@corp.test", phone: "+45 12 34 56 78", company: "Corp"),
            "Test Event 2027", "Following up from {0}");

        Assert.Equal(42, c.LeadId);
        Assert.Equal("Jane Doe", c.DisplayName);
        Assert.Equal("Corp", c.Company);
        Assert.NotNull(c.MailtoHref);
        Assert.StartsWith("mailto:jane%40corp.test", c.MailtoHref);
        Assert.Contains("subject=Following%20up%20from%20Test%20Event%202027", c.MailtoHref);
        Assert.Equal("tel:+4512345678", c.TelHref);   // spaces stripped, leading + kept
        Assert.True(c.IsContactable);
    }

    [Fact]
    public void Display_name_falls_back_first_last_then_email()
    {
        Assert.Equal("First Last",
            SponsorLeadContactLinkBuilder.Build(Lead(first: "First", last: "Last", email: "a@b.test"), "E").DisplayName);
        Assert.Equal("only@b.test",
            SponsorLeadContactLinkBuilder.Build(Lead(email: "only@b.test"), "E").DisplayName);
        Assert.Equal("(unnamed lead)",
            SponsorLeadContactLinkBuilder.Build(Lead(phone: "12345678"), "E").DisplayName);
    }

    [Fact]
    public void A_lead_with_no_usable_email_gets_no_mailto()
    {
        Assert.Null(SponsorLeadContactLinkBuilder.BuildMailto("not-an-email", "E", null));
        Assert.Null(SponsorLeadContactLinkBuilder.BuildMailto("", "E", null));
        Assert.Null(SponsorLeadContactLinkBuilder.BuildMailto("no@domain", "E", null)); // no TLD dot
    }

    [Fact]
    public void A_lead_with_no_dialable_phone_gets_no_tel()
    {
        Assert.Null(SponsorLeadContactLinkBuilder.BuildTel(""));
        Assert.Null(SponsorLeadContactLinkBuilder.BuildTel("ext."));   // nothing dialable
        Assert.Equal("tel:0045123", SponsorLeadContactLinkBuilder.BuildTel("(0045) 123"));
    }

    [Fact]
    public void A_lead_with_neither_is_not_contactable()
    {
        var c = SponsorLeadContactLinkBuilder.Build(Lead(full: "No Contact"), "E");
        Assert.Null(c.MailtoHref);
        Assert.Null(c.TelHref);
        Assert.False(c.IsContactable);
    }

    [Fact]
    public void Mailto_strips_newlines_so_a_crafted_field_cannot_inject_headers()
    {
        // A field carrying CR/LF must never produce a mailto with extra header lines.
        var href = SponsorLeadContactLinkBuilder.BuildMailto(
            "evil@corp.test\r\nBcc: victim@x.test", "E", null);
        // The shape check rejects the multi-line address outright.
        Assert.Null(href);

        // And an injected subject is encoded (no raw CR/LF survive in the href).
        var ok = SponsorLeadContactLinkBuilder.BuildMailto(
            "ok@corp.test", "E", "Hi\r\nBcc: x@y.test");
        Assert.NotNull(ok);
        Assert.DoesNotContain("\n", ok);
        Assert.DoesNotContain("\r", ok);
    }

    [Fact]
    public void Blank_subject_template_falls_back_to_a_plain_subject()
    {
        var href = SponsorLeadContactLinkBuilder.BuildMailto("ok@corp.test", "E", null);
        Assert.Contains("subject=Following%20up", href);
    }

    [Fact]
    public void Plus_only_keeps_one_leading_plus()
    {
        Assert.Equal("tel:+45123", SponsorLeadContactLinkBuilder.BuildTel("+45+123"));
    }
}
