namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>One generated file, ready to publish or to hand to a browser.</summary>
/// <param name="ContentKey">
/// 🔒 A hash of the DATA this file was built from — <b>not</b> of its bytes.
/// </param>
/// <remarks>
/// <para>⚠️ <b>Why the key exists, and it is not premature cleverness — a test caught this.</b>
/// An <c>.xlsx</c> is a ZIP, and neither ClosedXML's document properties nor the zip's per-entry
/// timestamps are reproducible: <b>building the same workbook twice from identical data produces
/// different bytes.</b> §6.4 mails the hotel file ON CHANGE, so a byte comparison would have
/// reported "changed" every single day and mailed an external venue contact every single day.</para>
///
/// <para>The key is computed from the rows the producer actually wrote — stable order, stable
/// formatting — so it changes when, and only when, the answer changes.</para>
/// </remarks>
/// <param name="Headline">
/// §6.3 — the one figure that tells the organizer whether this file looks right, e.g.
/// <c>"42 people"</c> or <c>"≈120 covers (estimate)"</c>.
/// <para>🔒 <b>The PRODUCER supplies it, because only the producer knows what the file counts.</b>
/// Reading the .xlsx back to count its rows would be a second implementation of the same rule (the
/// §767 failure) and it would report the spreadsheet's SHAPE rather than the data's MEANING:
/// breakfast has no sign-up and is an estimate, a rooming list counts guests, a Credly file counts
/// people. "Rows" is a different — and sometimes wrong — number for each of them.</para>
/// </param>
public sealed record GeneratedFile(
    string FileName, byte[] Content, string ContentType, string ContentKey, string Headline = "")
{
    public const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string CsvContentType = "text/csv";

    /// <summary>Hash the logical lines a file was built from.</summary>
    public static string KeyOf(IEnumerable<string> lines)
    {
        var joined = string.Join("\n", lines);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(joined)));
    }
}
