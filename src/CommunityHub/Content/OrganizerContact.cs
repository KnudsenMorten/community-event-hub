using CommunityHub.Core.Config;

namespace CommunityHub.Content;

/// <summary>
/// REQUIREMENTS §660 — the ONE rule for "which address do we tell a participant to write to?".
///
/// <para>Operator 2026-07-29, on the speaker My-tasks instructions: <i>"i also need the contact the
/// organizers to be a mailto link to info@expertslive.dk"</i>. The phrase appears on several pages
/// and was plain text on the one he was reading.</para>
///
/// <para><b>Why a shared resolver rather than a hard-coded address.</b> The address is per-edition
/// (<c>placeholders.organizerEmail</c> in the edition config) — hard-coding <c>info@expertslive.dk</c>
/// would be correct for ELDK27 and wrong for the next edition, which is the evergreen rule in
/// CLAUDE.md. The Contact page already resolved it this way; a second hand-rolled copy of
/// "load config → read placeholder → fall back" is exactly the drift that §656 and §637 were.</para>
/// </summary>
public static class OrganizerContact
{
    /// <summary>The shipped fallback, used when the edition config cannot be read or has no value.</summary>
    public const string Fallback = "info@expertslive.dk";

    /// <summary>
    /// The edition's organizer inbox. Never throws and never returns blank — a contact prompt that
    /// resolves to nothing is worse than one pointing at the shipped default.
    /// </summary>
    public static string Resolve(EventEditionConfigLoader cfg, EventConfigOptions opt)
    {
        try
        {
            var c = cfg.Load(opt.EventConfigPath);
            if (c.Placeholders.TryGetValue("organizerEmail", out var e) && !string.IsNullOrWhiteSpace(e))
                return e.Trim();
        }
        catch
        {
            // Config unreadable — fall through to the shipped address rather than showing nothing.
        }

        return Fallback;
    }
}
