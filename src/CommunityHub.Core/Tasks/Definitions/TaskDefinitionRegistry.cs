using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// What is true about ONE participant, for evaluating <see cref="TaskAudience"/>.
/// </summary>
/// <remarks>
/// 🔒 §684.15 — a plain value, deliberately. It lets the audience matrix test stand up synthetic
/// participants and assert exactly which tasks each one sees, without a database, a job run or a
/// seeder. §456 (an exhibitor-speaker seeing every sponsor task) becomes a red test rather than
/// something to remember.
/// </remarks>
public sealed record TaskAudienceFacts(
    ParticipantRole Role,
    IReadOnlyCollection<TaskAudiencePredicate> Predicates)
{
    public static TaskAudienceFacts For(
        ParticipantRole role, params TaskAudiencePredicate[] predicates) =>
        new(role, predicates);

    /// <remarks>
    /// 🔒 §708.3 — <c>Excludes</c> is checked LAST and wins. A predicate appearing in both lists
    /// therefore excludes, which is the safe direction: the exclusions encode tasks an operator
    /// removed by name (§456/§458), and re-granting one is a visible wrong, while withholding one
    /// shows up immediately in the audience matrix.
    /// </remarks>
    public bool Satisfies(TaskAudience audience) =>
        audience.Roles.Contains(Role)
        && audience.Requires.All(Predicates.Contains)
        && !audience.Excludes.Any(Predicates.Contains);
}

/// <summary>
/// §684.7 — THE registry. One plain C# collection of every task definition, for every role.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Constraint 1 is the acceptance test</b> (§684.5): <i>"model for tasks must be the same
/// for all roles, same method with deadlines, reminders, completion status, etc"</i>. Judge every
/// decision by whether a speaker task and a sponsor task now go through the SAME code. If a role
/// needs its own seeder, its own row partial or its own completion rule, the design has failed —
/// that is exactly today's situation.</para>
///
/// <para><b>Sponsor-first is a SEQUENCE, not a licence to special-case</b> (§601.1/§684.5). The
/// sponsor pass must produce a reusable mechanism; speaker / volunteer / media / partner / attendee
/// must then fall out cheaply. If they do not, stop and generalise before continuing.</para>
/// </remarks>
public sealed class TaskDefinitionRegistry
{
    private readonly Dictionary<string, TaskDefinition> _byKey;

    public TaskDefinitionRegistry(IEnumerable<TaskDefinition> definitions)
    {
        All = definitions.ToList();
        _byKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);
    }

    /// <summary>The registry as shipped — every role's definitions.</summary>
    /// <remarks>
    /// <para><b>Sponsor (§686.2 phase 1) + SPEAKER (§708).</b> The operator's approval gate that
    /// §686.5 held phase 2 behind is passed: he asked for the speaker set by name — <i>"what we did
    /// for /Sponsor/Tasks yesterday (new design) could also be implemented for /Speaker/Tasks"</i>.
    /// Volunteer / media / event partner / attendee still follow later.</para>
    ///
    /// <para>🔒 <b>Party and Master Class stay OUT</b> (§707.57b), deliberately — both are single
    /// self-contained tasks whose completion is already derived, so the registry would add authoring
    /// flexibility they do not need. Not an omission.</para>
    /// </remarks>
    public static TaskDefinitionRegistry Shipped { get; } =
        new(SponsorTaskDefinitions.All
            .Concat(SpeakerTaskDefinitions.All)
            .Concat(ParticipantTaskDefinitions.All));

    public IReadOnlyList<TaskDefinition> All { get; }

    public TaskDefinition? ByKey(string key) =>
        _byKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>
    /// Every definition this participant should hold. ONE walk over ONE list replaces the nine
    /// role seeders' worth of <c>if</c> statements.
    /// </summary>
    public IReadOnlyList<TaskDefinition> For(TaskAudienceFacts facts) =>
        All.Where(d => facts.Satisfies(d.Audience)).ToList();
}
