namespace CommunityHub.Core.Organizer;

/// <summary>
/// §334 (destructive-operation review GAP 1) — the SERVER-side half of a "type the word to
/// confirm" guard on an irreversible organizer action.
///
/// Before this, 14 destructive organizer actions were protected only by
/// <c>onsubmit="return confirm(...)"</c>. That guard has two holes the operator's
/// "I don't want to end in a disaster" question was really about:
///   1. one mis-click executes it — a dialog you dismiss by reflex is not a decision;
///   2. it is CLIENT-side, so a POST with JavaScript off, a replayed form, or a scripted
///      request skips it entirely and the action just runs.
///
/// The phrase check therefore lives HERE, on the server, and the page handler refuses when it
/// does not match. The typed input in the UI is only the way a human supplies it — turning
/// JavaScript off removes the convenience, not the guard.
///
/// Deliberately NOT applied to everything: a guard on a reversible action (revoke a sign-in
/// link, remove a schedule row) trains the operator to type the word without reading, which
/// makes it worthless where it matters. Reserved for the irreversible and the wide-blast-radius.
/// </summary>
public static class TypedConfirmation
{
    /// <summary>The word an operator types to arm an irreversible action.</summary>
    public const string DeletePhrase = "DELETE";

    /// <summary>Wider blast radius than one row — a whole company, a whole category, a bulk run.</summary>
    public const string ConfirmPhrase = "CONFIRM";

    /// <summary>
    /// True when <paramref name="typed"/> matches <paramref name="expected"/>. Case-insensitive
    /// and trim-tolerant on purpose: the guard exists to force a deliberate act, not to punish
    /// a trailing space or a caps-lock state. Null/blank never matches — a handler that forgets
    /// to bind the field fails CLOSED.
    /// </summary>
    public static bool Matches(string? typed, string expected) =>
        !string.IsNullOrWhiteSpace(typed)
        && string.Equals(typed.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The message to show when the phrase is missing or wrong. Says what to type.</summary>
    public static string Rejection(string expected, string what) =>
        $"Nothing was changed. To {what}, type {expected} in the confirmation box first — "
        + "this action cannot be undone.";
}
