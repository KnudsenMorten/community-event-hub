namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §643 — e-conomic rejected a call, and this carries WHY.
/// </summary>
/// <remarks>
/// <para><b>Why a typed exception rather than letting <c>HttpRequestException</c> through.</b> A
/// bare <c>EnsureSuccessStatusCode()</c> produces *"Response status code does not indicate success:
/// 400 (Bad Request)"* — which tells the operator nothing and, worse, is indistinguishable from a
/// genuine platform fault. e-conomic explains itself in the response body; this type carries that
/// explanation to the page so it can be shown instead of a 500.</para>
///
/// <para>🔒 <b>A rejected EDIT is not a crashed SERVER.</b> The Manage-contacts page returned HTTP
/// 500 for a contact e-conomic simply would not accept — so a routine data problem looked like the
/// hub was broken. Catching this type is what lets the page say what happened and keep the form
/// filled in.</para>
/// </remarks>
public sealed class EconomicApiException : Exception
{
    public EconomicApiException(string message) : base(message) { }
    public EconomicApiException(string message, Exception inner) : base(message, inner) { }
}
