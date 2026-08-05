namespace CommunityHub.Core.Domain;

/// <summary>
/// §828 — ONE EVENT POST IN THE POST REPO, keyed by its <see cref="Slug"/>.
///
/// <para>These are the <b>Type 5</b> (<c>SoMeTemplateKind.EventPost</c>) posts the operator writes
/// himself and lands as a markdown deck in the document library
/// (<c>DocLibraryPaths.EventSoMeTextFile</c>). The file is a <b>DROP-BOX, not a mirror</b>: it is
/// imported into this table and <b>CEH owns the words from that moment on</b> (§828, correcting
/// §824.24 where the file was recorded as read-only and not imported).</para>
///
/// <para><b>🔒 THE RULE THAT MATTERS MOST (§828.1): an imported post is NEVER overwritten by a later
/// import unless <see cref="AllowImportOverwrite"/> has been ticked on THAT post.</b> The key is the
/// slug — not the file, not the order in the file, not the title. Default is <c>false</c>, so a
/// re-import of a known slug is a <b>reported no-op</b>, never a silent skip and never a silent
/// replacement. The reason is concrete: an imported post is something he EDITS in the post editor
/// (§824.17), and a second import that quietly replaced it would throw the edit away — the same class
/// of loss the scheduler's "adds, never curates" rule (§824.21a) exists to prevent.</para>
///
/// <para><b>Distinct from <see cref="SoMePost"/>.</b> This is the REPO (the words, keyed by slug);
/// <see cref="SoMePost"/> is the QUEUE (one dated, publishable item). The scheduler plans Type 5
/// queue rows FROM these, carrying the slug as the queue row's <c>SubjectKey</c> (§828.5) — which is
/// the "subject CEH can derive one from" that §824.21 said was missing for Type 5.</para>
/// </summary>
public class EventSoMePost
{
    public int Id { get; set; }

    /// <summary>The edition this post belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// 🔑 THE IMPORT KEY — stated explicitly in the markdown (<c>`slug:` eldk27-networking</c>), never
    /// derived from the title. Unique per edition. §828.6: <i>"slug is in markdown and pairing is also
    /// in file"</i>, so there is no slugify convention to infer and no §767-shaped guess to get wrong.
    /// A file that RENAMES a slug creates a NEW post; it does not rename the old one (the safe
    /// direction — a rename cannot silently capture and overwrite an unrelated post).
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The post's title from the file (<c>`title:`</c>). Display only — the slug is the key.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The post body — the markdown between the header block and the next post separator.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// 🔒 §828.1 — THE PER-POST OVERWRITE TICK. False by default and reset to false after an
    /// overwrite is consumed, so allowing a replacement is always a <b>deliberate act on one post</b>
    /// rather than a mode that reprocesses the whole file.
    /// </summary>
    public bool AllowImportOverwrite { get; set; }

    // --- Import audit — so a run can be explained months later ---------------------------------

    /// <summary>When this post was first created by an import.</summary>
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When an import last REPLACED this post's content (i.e. the tick was on). Null when the post has
    /// never been overwritten — which, given the default, is the normal case.
    /// </summary>
    public DateTimeOffset? LastOverwrittenAt { get; set; }

    /// <summary>The file the post was last imported from — the deck can be re-landed under a new name.</summary>
    public string? SourceFileName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>
    /// The dated runs of this post — see <see cref="EventSoMePostOccurrence"/>. A post is NOT
    /// one-graphic-one-date: <c>eldk27-networking</c> alone runs six times with six graphics.
    /// </summary>
    public ICollection<EventSoMePostOccurrence> Occurrences { get; set; } = new List<EventSoMePostOccurrence>();
}

/// <summary>
/// §828.7 — ONE DATED RUN of an <see cref="EventSoMePost"/>, with the graphic that run uses.
///
/// <para><b>🔑 WHY THIS TABLE EXISTS AT ALL.</b> §828.5 recorded the shape as "each post is linked to
/// a graphic" — one-to-one. <b>Reading the actual file disproved that</b> (§828.7): the deck holds
/// <b>27 posts but 46 dated runs</b>, because the operator re-runs a post with a fresh graphic and a
/// fresh source photo. <c>eldk27-networking</c> runs SIX times (v1…v6). Storing one graphic per post
/// would have silently discarded five of those six — which is exactly why §828.4 refused to design
/// the schema before anyone had read the file.</para>
///
/// <para>The pairing is stated in the file's per-post <c>`dates:`</c> block, one line per run:
/// <c>- 2026-10-29 · `eldk27_20261029_eldk27-networking_v1_card.png` (It-peers_networking_lounge.JPG)</c>
/// — so, like the slug, it is READ and never derived from a filename convention.</para>
///
/// <para>⚠️ The summary table at the TOP of the file is NOT the source: two of its 46 rows name a
/// graphic that does not exist in the library. The per-post <c>dates:</c> blocks are 46/46 correct.
/// The parser reads the blocks and ignores the table (§828.7).</para>
/// </summary>
public class EventSoMePostOccurrence
{
    public int Id { get; set; }

    public int EventSoMePostId { get; set; }
    public EventSoMePost Post { get; set; } = null!;

    /// <summary>The date this run is planned for, as written in the file. A DATE, not an instant —
    /// the file states a day and the scheduler owns the time of day (§824.8 Q4).</summary>
    public DateOnly PostDate { get; set; }

    /// <summary>
    /// 1-based run number within the post (v1, v2, …), in file order. This is what becomes the queue
    /// row's <c>Occurrence</c>, so "have I already planned run 3 of this slug?" stays one lookup.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// The graphic for THIS run — a file name in <c>DocLibraryPaths.EventSoMeGraphics</c>
    /// (<c>Event/SoMe/Graphics-SoMe</c>). Stored as the bare name the file states, not a resolved
    /// URL: the folder is the registry's to locate, and a stored URL would rot when it moves.
    /// </summary>
    public string GraphicFileName { get; set; } = string.Empty;

    /// <summary>
    /// The source photograph the graphic was rendered from, when the file names one (some runs say
    /// "(background only)" and carry none). Provenance for him, not something CEH publishes.
    /// </summary>
    public string? SourcePhotoFileName { get; set; }
}
