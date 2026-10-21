using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CommunityHub.Core.Evaluation;

/// <summary>§1303 — settings for the organizer side of a kiosk tablet.</summary>
public sealed class EvaluationKioskOptions
{
    public const string SectionName = "EvaluationKiosk";

    /// <summary>
    /// The ONE organizer PIN: it opens the kiosk's setup screen and approves a one-tap enrolment.
    /// Held only here (an app setting), never on the tablet — the page sends what was typed and the
    /// server decides, so "the PIN on the device" and "the PIN in the backend" cannot drift apart.
    /// Empty = no PIN: setup opens as before and one-tap enrolment is unavailable.
    /// </summary>
    public string Pin { get; set; } = string.Empty;

    /// <summary>Intune device names offered for one-tap enrolment (the enrollment naming template).</summary>
    public string IntuneNamePrefix { get; set; } = "ELDK-";
}

/// <summary>
/// §1303 — checks the organizer PIN and stops it being guessed.
/// </summary>
/// <remarks>
/// A 4–6 digit PIN on a public, anonymous endpoint is only as strong as the attempt limit behind
/// it: <see cref="MaxFailures"/> wrong answers from one client lock that client out for
/// <see cref="Window"/>. ⚠️ The client is the IP address, and a venue's Wi-Fi puts every tablet
/// behind ONE address — five wrong tries from anyone there locks setup for all of them for 15
/// minutes. That is the accepted cost: keying on anything the caller chooses (a device id) would let
/// an attacker rotate it and guess freely. In-memory, per instance: a restart clears it, which only
/// ever errs towards the organizer.
/// </remarks>
public sealed class KioskPinGuard
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public enum Verdict { Ok, Wrong, LockedOut, NotConfigured }

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> _failures = new();

    public KioskPinGuard(TimeProvider clock) => _clock = clock;

    public Verdict Check(string? configuredPin, string? typedPin, string client)
    {
        if (string.IsNullOrWhiteSpace(configuredPin)) return Verdict.NotConfigured;

        var now = _clock.GetUtcNow();
        if (_failures.TryGetValue(client, out var f))
        {
            if (now - f.Since >= Window) _failures.TryRemove(client, out _);
            else if (f.Failures >= MaxFailures) return Verdict.LockedOut;
        }

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredPin.Trim()),
            Encoding.UTF8.GetBytes((typedPin ?? string.Empty).Trim()));
        if (ok)
        {
            _failures.TryRemove(client, out _);
            return Verdict.Ok;
        }

        _failures.AddOrUpdate(client, _ => (1, now),
            (_, old) => now - old.Since >= Window ? (1, now) : (old.Failures + 1, old.Since));
        return Verdict.Wrong;
    }
}
