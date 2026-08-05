using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Surveys;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.5 — the three post-event survey summaries are rebuilt daily into their §3.4 folders, and
/// nothing is mailed.
/// </summary>
/// <remarks>
/// <para>Work order: <i>"Summaries rebuilt daily from responses so far, written to the three §3.4
/// folders"</i> · <i>"No notification mail for these three."</i></para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class SurveySummaryPublishServiceTests
{
    private const int EventId = 1;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _folders = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Folder, string Name)> Uploads { get; } = new();

        public bool CanStore => true;
        public bool CanRead => true;

        private static string UrlFor(string folder, string name) => $"https://sp.test/{folder}/{name}";

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(folder, out var f)
                    ? f.Keys.Select(n => new SharePointFileRef($"{folder}|{n}", n, UrlFor(folder, n))).ToList()
                    : []);

        public Task<StoredFile> UploadToFolderAsync(
            string folder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            Uploads.Add((folder, fileName));
            if (!_folders.TryGetValue(folder, out var f)) _folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[fileName] = content;
            return Task.FromResult(new StoredFile($"{folder}/{fileName}", UrlFor(folder, fileName), "id"));
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Definitions : ISurveyDefinitionSource
    {
        private readonly Dictionary<string, SurveyDefinition> _defs;
        public Definitions(params string[] slugs) =>
            _defs = slugs.ToDictionary(s => s, Build, StringComparer.OrdinalIgnoreCase);

        private static SurveyDefinition Build(string slug) => new()
        {
            Slug = slug, Title = slug,
            Questions =
            [
                new SurveyQuestion
                {
                    Id = "rate", Kind = SurveyQuestionKind.Rating, Prompt = "How was it?",
                    ScaleMin = 1, ScaleMax = 5,
                },
                new SurveyQuestion
                {
                    Id = "one-thing", Kind = SurveyQuestionKind.FreeText, Prompt = "Change ONE thing",
                },
            ],
        };

        public SurveyDefinition? TryGet(string slug) => _defs.GetValueOrDefault(slug);
    }

    private static CommunityHubDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>().UseInMemoryDatabase(name).Options);

    private static readonly string[] AllSlugs =
        ["eldk27-post-attendee", "eldk27-post-speaker", "eldk27-post-sponsor"];

    private static (SurveySummaryPublishService Svc, FakeStore Store) NewService(
        CommunityHubDbContext db, ISurveyDefinitionSource defs, DateTimeOffset? now = null)
    {
        var store = new FakeStore();
        var svc = new SurveySummaryPublishService(
            db,
            new DocLibraryFilePublisher(store, TestDocLibrary.Resolver()),
            defs,
            new SurveyQuestionSummaryService(db),
            new SurveySummaryFileProducer(),
            new FixedClock(now ?? new DateTimeOffset(2027, 2, 20, 0, 0, 0, TimeSpan.Zero)));
        return (svc, store);
    }

    private static async Task AnswerAsync(
        CommunityHubDbContext db, string slug, int rating, string comment)
    {
        var response = new SurveyResponse
        {
            SurveySlug = slug, SelectedTrackId = string.Empty,
            SubmittedAt = new DateTimeOffset(2027, 2, 12, 0, 0, 0, TimeSpan.Zero),
        };
        db.SurveyResponses.Add(response);
        db.SurveyResponseAnswers.Add(new SurveyResponseAnswer
        {
            Response = response, QuestionId = "rate", Kind = SurveyQuestionKind.Rating, Rating = rating,
        });
        db.SurveyResponseAnswers.Add(new SurveyResponseAnswer
        {
            Response = response, QuestionId = "one-thing", Kind = SurveyQuestionKind.FreeText, Text = comment,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Each survey's summary lands in ITS OWN §3.4 folder — they are not interchangeable.</summary>
    [Fact]
    public async Task Each_summary_is_written_to_its_own_folder()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        var (svc, store) = NewService(db, new Definitions(AllSlugs));

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ran);
        Assert.Equal(3, result.Published);
        Assert.Empty(result.Failures);

        var paths = TestDocLibrary.Resolver();
        foreach (var (slug, key) in SurveySummaryPublishService.FolderBySlug)
        {
            Assert.True(paths.TryResolve(key, out var folder));
            Assert.Contains(store.Uploads, u =>
                u.Folder == folder && u.Name == SurveySummaryFileProducer.FileNameFor(slug));
        }
    }

    /// <summary>
    /// ⚠️ A survey NOBODY has answered still gets a file. An absent file reads as "the job is
    /// broken"; a file saying zero reads as "nobody has answered yet", which is the fact.
    /// </summary>
    [Fact]
    public async Task A_survey_with_no_responses_still_gets_a_file_saying_zero()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        var (svc, _) = NewService(db, new Definitions(AllSlugs));

        await svc.RunAsync(EventId);

        var state = await db.LogisticsFileStates
            .FirstAsync(s => s.FileName == SurveySummaryFileProducer.FileNameFor("eldk27-post-attendee"));
        Assert.Equal("0 responses", state.Headline);
    }

    /// <summary>
    /// 🔒 Republished only when the ANSWERS changed. A daily identical rewrite makes the library's
    /// modified stamp meaningless — and that stamp is how an organizer sees whether anybody has
    /// responded since they last looked.
    /// </summary>
    [Fact]
    public async Task A_second_run_over_unchanged_answers_writes_NOTHING()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        var (svc, store) = NewService(db, new Definitions(AllSlugs));

        await svc.RunAsync(EventId);
        var after = store.Uploads.Count;

        var second = await svc.RunAsync(EventId);

        Assert.Equal(after, store.Uploads.Count);
        Assert.Equal(0, second.Published);
        Assert.Equal(3, second.Unchanged);
    }

    /// <summary>A new response makes that ONE survey republish — and only that one.</summary>
    [Fact]
    public async Task A_new_response_republishes_only_that_survey()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        var (svc, store) = NewService(db, new Definitions(AllSlugs));

        await svc.RunAsync(EventId);
        store.Uploads.Clear();

        await AnswerAsync(db, "eldk27-post-attendee", 5, "More coffee");
        var second = await svc.RunAsync(EventId);

        Assert.Equal(1, second.Published);
        Assert.Equal(2, second.Unchanged);
        Assert.Single(store.Uploads);
        Assert.Equal(
            SurveySummaryFileProducer.FileNameFor("eldk27-post-attendee"),
            store.Uploads[0].Name);
    }

    /// <summary>
    /// 🔒 A definition that will not load is NAMED, not swallowed — and it does not stop the other
    /// two. §773.1 was exactly a definition failing to load and turning into a silence nobody saw.
    /// </summary>
    [Fact]
    public async Task A_survey_whose_definition_will_not_load_is_named_and_the_others_still_publish()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        // The sponsor definition is missing from the source.
        var (svc, _) = NewService(db, new Definitions("eldk27-post-attendee", "eldk27-post-speaker"));

        var result = await svc.RunAsync(EventId);

        Assert.Equal(2, result.Published);
        Assert.Contains(result.Skipped, s => s.Contains("eldk27-post-sponsor"));
        Assert.Empty(result.Failures);
    }

    /// <summary>A host that cannot write the library says so, rather than reporting a clean run.</summary>
    [Fact]
    public async Task A_host_that_cannot_write_reports_itself_inactive()
    {
        using var db = NewDb($"s-{Guid.NewGuid():N}");
        var svc = new SurveySummaryPublishService(
            db,
            new DocLibraryFilePublisher(new NullSharePointFileStore(), TestDocLibrary.Resolver()),
            new Definitions(AllSlugs),
            new SurveyQuestionSummaryService(db),
            new SurveySummaryFileProducer());

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ran);
        Assert.NotNull(result.InactiveReason);
    }
}
