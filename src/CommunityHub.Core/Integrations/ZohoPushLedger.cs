using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>One field we have sent to Zoho that Zoho has not given back.</summary>
/// <param name="Field">The ledger key, e.g. <c>sponsor:description</c>.</param>
/// <param name="Hash">A hash of the value sent — the value itself can be long, and is the sponsor's copy.</param>
/// <param name="Attempts">How many consecutive runs have sent this same value without it arriving.</param>
public sealed record ZohoUnconfirmedPush(string Field, string Hash, int Attempts);

/// <summary>What the run should SAY about a write, given how often it has already said it.</summary>
public enum ZohoPushReport
{
    /// <summary>An ordinary "we wrote this" line.</summary>
    Normal,

    /// <summary>The one warning that this value keeps not landing.</summary>
    Warn,

    /// <summary>Already warned — the push continues, the mail stays quiet.</summary>
    Silent,
}

/// <summary>
/// §1175 — remember what we sent Zoho, so a write that never lands is REPORTED ONCE instead of
/// announced every ten minutes for ever.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-03, on the same sponsors appearing in the ops mail run after run: <i>"but
/// the reconsile must verify the existing value and only change it different"</i> — it already does.
/// The comparison is correct and the read/write keys match, so this is not the §1140 shape. The fault
/// is one level up: CEH announced the same write for ever without noticing it was the same write.</para>
///
/// <para>🔑 <b>Two causes look identical from here</b>, and neither is fixable by comparing harder:
/// the write lands as an unpublished draft while the API returns the published value, or Zoho accepts
/// the field and silently discards it — §791.3 measured exactly that for <c>company_social_pages</c>:
/// four writes, four 200s, gone on the next GET. In both cases the value we hold is right, the value
/// Zoho returns is blank, and the comparison honestly says "different".</para>
///
/// <para>🔴 <b>THIS LEDGER DOES NOT STOP THE PUSH, AND MUST NEVER BE CHANGED TO.</b> §784.13 is
/// explicit — <i>"Do NOT reintroduce a 'push once' memo to reduce chatter"</i> — and it was written
/// over a real bill: the dead <c>ZohoSocialPushedHash</c> recorded INTENT, marked a field done when
/// it had not landed, and left <b>ten of thirteen sponsors' LinkedIn URLs blank in Zoho
/// indefinitely</b>. A first draft of THIS class made the same mistake, capping the push at three
/// attempts. ⚠️ Had that shipped, §1087 — Zoho repairing the social endpoint on 2026-08-16 — would
/// have healed nothing, because CEH would have stopped sending long before.</para>
///
/// <para>⇒ The push stays unconditional, idempotent and self-healing: it fires only while Zoho is
/// actually blank, and the moment the value lands the live comparison stops it by itself. What this
/// ledger changes is the <b>MAIL</b>, which is the thing that was actually costing him attention —
/// after <see cref="ReportAfterAttempts"/> consecutive identical sends the "we wrote this" line
/// becomes one warning naming the two likely causes, and then goes quiet. Same lesson as §302: a
/// correct mechanism reported too loudly becomes the problem.</para>
///
/// <para>🔒 <b>A value that CHANGES resets the count</b> — the ledger tracks a specific value, not a
/// field, so a sponsor's edit is a new fact and earns its own announcement. 🔒 <b>Arriving CLEARS the
/// entry</b>, so a healthy field leaves no trace and a later relapse is reported afresh.</para>
/// </remarks>
public static class ZohoPushLedger
{
    /// <summary>
    /// How many consecutive sends of the SAME value before the mail says so and then falls silent.
    /// </summary>
    /// <remarks>
    /// ⚠️ Three, not one. A single unseen write is ordinary — Zoho may be eventually consistent, and
    /// a draft awaiting publication resolves by itself. Three consecutive runs of the same value is
    /// not a delay, it is a refusal; warning at one would flag every ordinary push as trouble.
    /// </remarks>
    public const int ReportAfterAttempts = 3;

    /// <summary>Ledger key for a field on the company's primary sponsor record.</summary>
    public static string SponsorKey(string field) => $"sponsor:{field}";

    /// <summary>Ledger key for a field on the company's exhibitor record.</summary>
    /// <remarks>
    /// 🔒 Prefixed because BOTH records live on one <see cref="SponsorInfo"/> row and both have a
    /// <c>website_url</c>. Unprefixed, an exhibitor website arriving would clear the sponsor's
    /// entry and the two would silence each other.
    /// </remarks>
    public static string ExhibitorKey(string field) => $"exhibitor:{field}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A short, stable hash of a value — the ledger stores this, never the text.</summary>
    public static string HashOf(string? value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes((value ?? string.Empty).Trim())))[..16];

    /// <summary>Everything currently unconfirmed for this company. Never throws.</summary>
    public static IReadOnlyList<ZohoUnconfirmedPush> Read(SponsorInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ZohoUnconfirmedPushesJson))
            return Array.Empty<ZohoUnconfirmedPush>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ZohoUnconfirmedPush>>(
                info.ZohoUnconfirmedPushesJson!, Json);
            return parsed?.Where(p => !string.IsNullOrWhiteSpace(p.Field)).ToList()
                ?? (IReadOnlyList<ZohoUnconfirmedPush>)Array.Empty<ZohoUnconfirmedPush>();
        }
        catch (JsonException)
        {
            // 🔒 Unreadable = nothing remembered (§1140b: "I cannot read it" is not a fact about the
            // value). The cost is one extra announcement; the cost of throwing is the whole
            // reconcile stopping for every company over one malformed row.
            return Array.Empty<ZohoUnconfirmedPush>();
        }
    }

    private static void Write(SponsorInfo info, List<ZohoUnconfirmedPush> entries) =>
        info.ZohoUnconfirmedPushesJson = entries.Count == 0
            ? null
            : JsonSerializer.Serialize(entries, Json);

    /// <summary>Record that we sent this value and Zoho did not have it beforehand.</summary>
    /// <returns>How many consecutive times this exact value has now been sent.</returns>
    public static int RecordSent(SponsorInfo info, string field, string? valueSent)
    {
        var hash = HashOf(valueSent);
        var entries = Read(info).ToList();
        var i = entries.FindIndex(e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase));

        var attempts = 1;
        if (i >= 0)
        {
            // A changed value starts again at one: it is a new fact, not a repeat.
            attempts = string.Equals(entries[i].Hash, hash, StringComparison.Ordinal)
                ? entries[i].Attempts + 1
                : 1;
            entries[i] = new ZohoUnconfirmedPush(field, hash, attempts);
        }
        else
        {
            entries.Add(new ZohoUnconfirmedPush(field, hash, attempts));
        }

        Write(info, entries);
        return attempts;
    }

    /// <summary>How this send should be reported, given how many times we have now sent it.</summary>
    public static ZohoPushReport ReportFor(int attempts) =>
        attempts < ReportAfterAttempts ? ZohoPushReport.Normal
        : attempts == ReportAfterAttempts ? ZohoPushReport.Warn
        : ZohoPushReport.Silent;

    /// <summary>
    /// The value arrived (or is no longer wanted) — forget it. Returns true when something changed.
    /// </summary>
    public static bool Clear(SponsorInfo info, string field)
    {
        var entries = Read(info).ToList();
        if (entries.RemoveAll(e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;

        Write(info, entries);
        return true;
    }

    /// <summary>Fields that have hit the limit — still being pushed, no longer being announced.</summary>
    public static IReadOnlyList<ZohoUnconfirmedPush> Stuck(SponsorInfo info) =>
        Read(info).Where(e => e.Attempts >= ReportAfterAttempts).ToList();
}
