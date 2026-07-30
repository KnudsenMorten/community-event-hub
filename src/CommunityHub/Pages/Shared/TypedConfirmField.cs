namespace CommunityHub.Pages.Shared;

/// <summary>
/// §334 — view model for the <c>_TypedConfirm</c> partial: the phrase the operator must type
/// before an irreversible action is allowed, and what it will do.
/// </summary>
/// <param name="Phrase">The word to type — <c>TypedConfirmation.DeletePhrase</c> / <c>ConfirmPhrase</c>.</param>
/// <param name="What">One line naming the consequence, e.g. "Deletes the category and every task under it."</param>
/// <param name="FieldName">Form field name the handler binds. Defaults to the shared convention.</param>
/// <param name="InputId">DOM id — must be unique when a page renders more than one.</param>
public sealed record TypedConfirmField(
    string Phrase,
    string What,
    string FieldName = "confirmPhrase",
    string InputId = "confirmPhrase");
