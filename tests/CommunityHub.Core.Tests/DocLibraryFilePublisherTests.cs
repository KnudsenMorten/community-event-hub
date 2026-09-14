using System.Text;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.4 — the logistics files rebuild daily; the mails go weekly, and the hotel mail goes on CHANGE.
/// </summary>
/// <remarks>
/// 🔒 <b>"Did this actually change?" is the load-bearing question.</b> Work-order §6.4:
/// <i>"Idempotent: unchanged input produces an identical file and, for change-triggered mail, no
/// send."</i> Get it wrong and a daily rebuild mails an external hotel contact every day — the
/// fastest possible way to teach a venue to ignore CEH.
/// </remarks>
public sealed class DocLibraryFilePublisherTests
{
    private const string Key = DocLibraryPaths.BcFood;

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Uploads { get; } = new();
        public List<string> Downloads { get; } = new();
        public Exception? UploadThrows { get; set; }

        public void Seed(string folder, string name, byte[] content)
        {
            if (!_folders.TryGetValue(folder, out var f)) _folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[name] = content;
        }

        public byte[]? Get(string folder, string name) =>
            _folders.TryGetValue(folder, out var f) && f.TryGetValue(name, out var b) ? b : null;

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(relativeFolder, out var f)
                    ? f.Keys.Select(n => new SharePointFileRef($"{relativeFolder}|{n}", n, string.Empty)).ToList()
                    : []);

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
        {
            Downloads.Add(itemId);
            var parts = itemId.Split('|');
            return Task.FromResult(Get(parts[0], parts[1]));
        }

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            if (UploadThrows is not null) throw UploadThrows;
            Uploads.Add(fileName);
            Seed(relativeFolder, fileName, content);
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", string.Empty, "id"));
        }

        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static (DocLibraryFilePublisher Pub, FakeStore Store, string Folder) New()
    {
        var paths = TestDocLibrary.Resolver();
        paths.TryResolve(Key, out var folder);
        var store = new FakeStore();
        return (new DocLibraryFilePublisher(store, paths), store, folder);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A produced file whose CONTENT KEY is derived from its logical data.</summary>
    private static GeneratedFile File(string name, string data) =>
        new(name, Bytes(data), GeneratedFile.XlsxContentType, GeneratedFile.KeyOf([data]));

    [Fact]
    public async Task A_file_that_does_not_exist_yet_is_CREATED_and_reported_as_changed()
    {
        var (pub, store, folder) = New();

        var result = await pub.PublishAsync(Key, File("eldk27-lunch-day1-preday.xlsx", "a"), null);

        Assert.True(result.Ok);
        Assert.True(result.Changed);
        Assert.True(result.Written);
        Assert.Equal(folder, result.Folder);
        Assert.Single(store.Uploads);
    }

    /// <summary>
    /// 🔒 THE ONE THAT STOPS THE DAILY MAIL. Unchanged DATA ⇒ no write and no "changed", so the
    /// change-triggered hotel mail stays silent.
    /// </summary>
    [Fact]
    public async Task UNCHANGED_data_is_NOT_rewritten_and_NOT_reported_as_changed()
    {
        var (pub, store, folder) = New();
        var file = File("eldk27-party-preday.xlsx", "same");
        store.Seed(folder, file.FileName, file.Content);

        var result = await pub.PublishAsync(Key, file, previousKey: file.ContentKey);

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.False(result.Written);
        Assert.Empty(store.Uploads);
    }

    [Fact]
    public async Task CHANGED_data_is_written_and_reported_as_changed()
    {
        var (pub, store, folder) = New();
        var old = File("eldk27-party-preday.xlsx", "old");
        store.Seed(folder, old.FileName, old.Content);

        var result = await pub.PublishAsync(
            Key, File("eldk27-party-preday.xlsx", "new"), previousKey: old.ContentKey);

        Assert.True(result.Changed);
        Assert.Equal("new", Encoding.UTF8.GetString(store.Get(folder, "eldk27-party-preday.xlsx")!));
    }

    /// <summary>
    /// 🔒 A file that has VANISHED from the library is restored even though the data is unchanged —
    /// "we published it once" is not "it is there". It is Written but NOT Changed, so the venue is
    /// not mailed about a file whose contents they already have.
    /// </summary>
    [Fact]
    public async Task A_file_deleted_from_the_library_is_RESTORED_without_being_reported_as_changed()
    {
        var (pub, store, _) = New();
        var file = File("eldk27-expo-rental-tv.xlsx", "same");

        var result = await pub.PublishAsync(Key, file, previousKey: file.ContentKey);

        Assert.True(result.Written);
        Assert.False(result.Changed);
        Assert.Single(store.Uploads);
    }

    /// <summary>
    /// ⚠️ THE TRADE-OFF, pinned so it is a decision and not a surprise: a file hand-edited IN the
    /// library is NOT detected, because the key describes our data rather than their copy. The next
    /// real data change overwrites it. A generated file is not a place to keep hand edits.
    /// </summary>
    [Fact]
    public async Task A_hand_edit_in_the_library_is_NOT_detected_until_the_data_changes()
    {
        var (pub, store, folder) = New();
        var file = File("eldk27-breakfast-day2-mainday.xlsx", "generated");
        store.Seed(folder, file.FileName, Bytes("hand-edited"));

        var unchanged = await pub.PublishAsync(Key, file, previousKey: file.ContentKey);
        Assert.False(unchanged.Written);

        var changed = await pub.PublishAsync(
            Key, File(file.FileName, "new data"), previousKey: file.ContentKey);
        Assert.True(changed.Changed);
        Assert.Equal("new data", Encoding.UTF8.GetString(store.Get(folder, file.FileName)!));
    }

    [Fact]
    public async Task A_failure_is_REPORTED_as_data_so_one_file_cannot_stop_the_run()
    {
        var (pub, store, _) = New();
        store.UploadThrows = new InvalidOperationException("Graph said no");

        var result = await pub.PublishAsync(Key, File("eldk27-award.xlsx", "a"), null);

        Assert.False(result.Ok);
        Assert.False(result.Changed);
        Assert.Contains("Graph said no", result.Error!);
    }

    [Fact]
    public async Task An_unregistered_path_key_is_refused_by_name()
    {
        var (pub, _, _) = New();

        var result = await pub.PublishAsync("NotARegisteredKey", File("x.xlsx", "a"), null);

        Assert.False(result.Ok);
        Assert.Contains("NotARegisteredKey", result.Error!);
    }
}

/// <summary>§6.4 / §3.5 — the generated file names, which the venue reads.</summary>
public sealed class LogisticsFileNameTests
{
    [Theory]
    [InlineData("ELDK27", "eldk27-award.xlsx")]
    [InlineData("eldk27", "eldk27-award.xlsx")]
    // 🔒 The prefix is an EVENT SETTING, never a constant: a second edition on the same library
    // must not overwrite the first one's files.
    [InlineData("ELDK28", "eldk28-award.xlsx")]
    [InlineData("", "event-award.xlsx")]        // visibly wrong beats silently colliding
    public void The_award_file_is_prefixed_with_the_event_short_name(string code, string expected)
    {
        Assert.Equal(expected, LogisticsFileNames.Award(code));
    }

    [Fact]
    public void The_food_files_carry_the_day_the_way_the_venue_reads_it()
    {
        // Both halves matter: "day1" alone does not say which day of the event, "preday" alone
        // does not sort.
        Assert.Equal("eldk27-breakfast-day1-preday.xlsx",
            LogisticsFileNames.Breakfast("ELDK27", LogisticsDay.PreDay));
        Assert.Equal("eldk27-lunch-day2-mainday.xlsx",
            LogisticsFileNames.Lunch("ELDK27", LogisticsDay.MainDay));
        Assert.Equal("eldk27-appreciationdinner-preday.xlsx",
            LogisticsFileNames.AppreciationDinner("ELDK27"));
        Assert.Equal("eldk27-party-preday.xlsx", LogisticsFileNames.Party("ELDK27"));
    }

    [Theory]
    [InlineData("Speaker", "eldk27-credly-speaker.xlsx")]
    [InlineData("Master Class Speaker", "eldk27-credly-master-class-speaker.xlsx")]
    public void Credly_is_one_pair_per_role_slugged(string role, string expected)
    {
        Assert.Equal(expected, LogisticsFileNames.Credly("ELDK27", role, ".xlsx"));
        Assert.Equal(expected.Replace(".xlsx", ".csv"),
            LogisticsFileNames.Credly("ELDK27", role, "csv"));   // a missing dot is added
    }

    [Fact]
    public void A_hotel_file_is_named_per_hotel_so_two_hotels_cannot_collide()
    {
        Assert.Equal("eldk27-hotel-scandic-copenhagen.xlsx",
            LogisticsFileNames.Hotel("ELDK27", "Scandic Copenhagen"));
        Assert.NotEqual(
            LogisticsFileNames.Hotel("ELDK27", "Scandic Copenhagen"),
            LogisticsFileNames.Hotel("ELDK27", "Scandic Sydhavnen"));
    }
}

/// <summary>
/// §6.4 / §5.6 — who a logistics notification is actually sent to, while the reports are unapproved.
/// </summary>
/// <remarks>
/// 🔒 Operator 2026-08-02: <i>"the logistics mails should go to mok@expertslive.dk until I approve
/// them"</i> — narrowed by him to the <b>Excel notification mails only</b>. The recipients in §5.6
/// are EXTERNAL (a venue mailbox, a hotel contact), and an unreviewed spreadsheet that reaches one
/// of them cannot be recalled.
/// </remarks>
public sealed class LogisticsRecipientsTests
{
    [Fact]
    public void UNAPPROVED_sends_everything_to_the_review_mailbox()
    {
        var r = new LogisticsRecipients { VenueOperations = "venue@example.test" };

        Assert.False(r.ApprovedForRealRecipients);          // the shipped default
        Assert.Equal(LogisticsRecipients.DefaultReviewMailbox, r.Resolve("venue@example.test"));
        Assert.Equal(LogisticsRecipients.DefaultReviewMailbox, r.Resolve("some.hotel@example.test"));
        Assert.Contains("not approved yet", r.ExplainFor("venue@example.test"));
    }

    [Fact]
    public void APPROVED_uses_the_real_recipient()
    {
        var r = new LogisticsRecipients { ApprovedForRealRecipients = true };

        Assert.Equal("venue@example.test", r.Resolve("venue@example.test"));
        Assert.Contains("Sent to venue@example.test", r.ExplainFor("venue@example.test"));
    }

    /// <summary>
    /// ⚠️ Approved but a recipient was never configured: the review mailbox, not nowhere. Sending
    /// nowhere would hide a missing setting behind a job that reports success.
    /// </summary>
    [Fact]
    public void APPROVED_but_unconfigured_falls_back_to_the_review_mailbox()
    {
        var r = new LogisticsRecipients { ApprovedForRealRecipients = true };

        Assert.Equal(LogisticsRecipients.DefaultReviewMailbox, r.Resolve(null));
        Assert.Equal(LogisticsRecipients.DefaultReviewMailbox, r.Resolve("   "));
        Assert.Contains("No recipient configured", r.ExplainFor(null));
    }
}
