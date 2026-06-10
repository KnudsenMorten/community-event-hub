namespace CommunityHub.Core.Domain;

/// <summary>
/// Self-assessed competency the respondent picks for each topic in step 3
/// of the survey wizard. Three tiers, professional only -- no Introduction/
/// Beginner connotation. Numbers (300 / 400 / 500) follow Microsoft-event
/// convention so speakers in the Call for Speakers immediately recognise
/// the level when pitching.
///
/// Integer values are the contract with the wire (radio button value
/// attribute) and the DB column. Reordering or renumbering breaks both.
/// </summary>
public enum SurveyLevel
{
    /// <summary>Advanced -- level 300. "I can do the basics + want production-grade depth."</summary>
    Advanced = 1,
    /// <summary>Expert -- level 400. "I've shipped this + want harder patterns."</summary>
    Expert   = 2,
    /// <summary>Black Belt -- level 500. "I architect / teach this + want the deepest cut."</summary>
    BlackBelt = 3,
}
