using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1295 test support — put one edition's volunteer task assignment in the RELEASED state.
/// </summary>
/// <remarks>
/// The catalog default is OFF at Ring 1 (assignment held while volunteers onboard), so a test that
/// asserts assignment BEHAVIOUR has to say that an organizer released it first — otherwise it is
/// asserting the gate, not the thing it meant to test. One call makes that explicit at the seed,
/// rather than five copies of the same FeatureSetting row.
/// </remarks>
internal static class VolunteerAssignmentRelease
{
    /// <summary>Switch volunteer task assignment ON and release it to Broad for this edition.</summary>
    public static void ToBroad(CommunityHubDbContext db, int eventId)
    {
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = eventId,
            FeatureKey = VolunteerAssignmentGate.FeatureKey,
            Enabled = true,
            ReleasedToRingOverride = Ring.Broad,
        });
        db.SaveChanges();
    }
}
