namespace CommunityHub.Core.Integrations;

/// <summary>
/// The event's own LinkedIn company page — ONE constant for every link to it (the follow card on the
/// speaker and sponsor announcement pages, the announcement notice mail, the speaker share text).
/// </summary>
/// <remarks>
/// 🔴 Operator 2026-10-01: the speaker page's "Follow" button was broken. The card and the mail
/// linked <c>/company/experts-live-denmark/</c>, a slug that does not exist (LinkedIn 404), while the
/// graphics share text linked the real <c>/company/expertslivedk</c>. Three copies of one URL had
/// drifted; one constant cannot.
/// </remarks>
public static class EventLinkedInPage
{
    public const string Url = "https://www.linkedin.com/company/expertslivedk";
}
