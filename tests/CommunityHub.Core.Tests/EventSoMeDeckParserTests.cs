using System;
using System.Linq;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §828 — the event-post deck format.
///
/// <para>The fixture below is modelled line for line on the real deck
/// (<c>ELDK27-LinkedIn-posts.md</c>, read from the document library 2026-08-05) and deliberately
/// reproduces its <b>traps</b>, not just its happy path:</para>
/// <list type="bullet">
///   <item>a summary table at the top whose graphic names DISAGREE with the posts (two of the real
///         file's 46 rows do exactly this) — the parser must ignore the table;</item>
///   <item>a post with SIX dated runs, because the shape is one post → many occurrences;</item>
///   <item>a "(background only)" run that names no photograph;</item>
///   <item>a <c>graphics:</c> block that itself contains backticked .png names and middle dots —
///         reading those as runs would double every occurrence;</item>
///   <item>an appendix after the last post, which must not be swallowed into its body.</item>
/// </list>
/// </summary>
public sealed class EventSoMeDeckParserTests
{
    private const string Deck = """
# ELDK27 — LinkedIn posts

Posting calendar — 7 posts.

| # | Date | Post | Graphic | Photo |
|---|---|---|---|---|
| 1 | 2026-08-04 Tue | 1 · ELDK27 recap | `eldk27_20260804_eldk27-announcement_v1_full.png` | ELDK_lamp.JPG |
| 2 | 2026-10-29 Thu | 19 · Re-union | `eldk27_20261029_eldk27-networking_v1_card.png` | It-peers.JPG |
---

## Post 1 — Back from summer: ELDK27 recap

`title:` ELDK27 recap after summer
`slug:` eldk27-announcement
`dates:`

  - 2026-08-04 · `eldk27_20260804_eldk27-announcement_v1_card.png` (ELDK_principles.JPG)

`graphics:`
- **Style:** framed card
- **Photos used (1):**
  - v1 · `ELDK_principles.JPG` → `eldk27_20260804_eldk27-announcement_v1_card.png`
- **Photo brief (if a new or AI image is needed):** `eldk27-announcement.jpg` — wide shot

Welcome back from summer — and a quick reminder of what is waiting for you in February.

🎟️ Tickets go on sale Tuesday 11 August.

#ELDK27 #ExpertsLiveDK

---

## Post 19 — The Danish IT Class Re-union

`title:` The Danish IT Class Re-union
`slug:` eldk27-networking
`dates:`

  - 2026-10-29 · `eldk27_20261029_eldk27-networking_v1_card.png` (It-peers_networking_lounge.JPG)
  - 2026-12-10 · `eldk27_20261210_eldk27-networking_v2_card.png` (eldk27-networking.JPG)
  - 2027-01-21 · `eldk27_20270121_eldk27-networking_v6_card.png` (AttendeesTalking2.JPG)

`graphics:`
- **Style:** framed card
- **Photos used (3):**
  - v1 · `It-peers_networking_lounge.JPG` → `eldk27_20261029_eldk27-networking_v1_card.png`

People keep calling ELDK "the Danish IT class re-union". We are leaning into it.

---

## Post 10 — Coca-Cola all day

`title:` Coca-Cola all day, both days
`slug:` eldk27-coca-cola
`dates:`

  - 2027-01-12 · `eldk27_20270112_eldk27-coca-cola_v1_typo.png` ((background only))

`graphics:`
- **Style:** typographic

We heard you: you want Coca-Cola. All day. Both days. 🥤

---

### How placement is validated
Every full-bleed graphic is analysed before the text is drawn.

### Importing into EventHub
`eldk27-social-calendar.csv` is the import file.
""";

    private static EventSoMeDeckParseResult Parsed() => EventSoMeDeckParser.Parse(Deck);

    [Fact]
    public void Reads_every_post_and_keys_it_by_the_slug_the_file_states()
    {
        var result = Parsed();

        Assert.Equal(
            new[] { "eldk27-announcement", "eldk27-networking", "eldk27-coca-cola" },
            result.Posts.Select(p => p.Slug).ToArray());

        // The title is READ, never derived from the heading (the heading says "Back from summer").
        Assert.Equal("ELDK27 recap after summer", result.Posts[0].Title);
    }

    [Fact]
    public void A_post_carries_all_of_its_dated_runs_not_just_the_first()
    {
        // 🔑 The correction that came out of reading the real file: 27 posts but 46 runs. Storing one
        // graphic per post would silently discard five of this post's six real runs.
        var networking = Parsed().Posts.Single(p => p.Slug == "eldk27-networking");

        Assert.Equal(3, networking.Occurrences.Count);
        Assert.Equal(new DateOnly(2026, 10, 29), networking.Occurrences[0].Date);
        Assert.Equal(
            "eldk27_20261210_eldk27-networking_v2_card.png",
            networking.Occurrences[1].GraphicFileName);
        Assert.Equal("AttendeesTalking2.JPG", networking.Occurrences[2].SourcePhotoFileName);
    }

    [Fact]
    public void The_graphics_block_is_not_mistaken_for_more_dated_runs()
    {
        // The `graphics:` block repeats the same filenames with the same middle-dot separator. If it
        // were read as runs, this post would report two occurrences instead of one.
        var announcement = Parsed().Posts.Single(p => p.Slug == "eldk27-announcement");

        Assert.Single(announcement.Occurrences);
        Assert.Equal(
            "eldk27_20260804_eldk27-announcement_v1_card.png",
            announcement.Occurrences[0].GraphicFileName);
    }

    /// <summary>
    /// 🔒 REGRESSION, and the reason the fixture writes DOUBLE parentheses: the real deck spells the
    /// text-only runs <c>((background only))</c>. A strict <c>\(([^)]*)\)</c> match — which read
    /// correctly and passed a single-parenthesis fixture — rejected the whole line, so those runs
    /// vanished: <b>41 of 46 imported, with no error raised</b>. Only running the parser over the
    /// actual file exposed it. The run must survive; only the photo is absent.
    /// </summary>
    [Fact]
    public void Background_only_is_prose_and_never_stored_as_a_photo_file()
    {
        var cola = Parsed().Posts.Single(p => p.Slug == "eldk27-coca-cola");

        Assert.Single(cola.Occurrences);
        Assert.Equal(
            "eldk27_20270112_eldk27-coca-cola_v1_typo.png",
            cola.Occurrences[0].GraphicFileName);
        Assert.Null(cola.Occurrences[0].SourcePhotoFileName);
    }

    [Theory]
    [InlineData("(ELDK_lamp.JPG)", "ELDK_lamp.JPG")]      // the ordinary form
    [InlineData("((background only))", null)]              // the real text-only form
    [InlineData("(background only)", null)]                // single-paren prose, tolerated too
    [InlineData("", null)]                                 // no trailing field at all
    public void Every_spelling_of_the_trailing_photo_field_is_read_correctly(string tail, string? expected)
    {
        var deck = $$"""
## Post 1 — X

`title:` X
`slug:` x-slug
`dates:`

  - 2026-08-04 · `x_v1_card.png` {{tail}}

`graphics:`
- **Style:** card

Body.

---
""";

        var post = Assert.Single(EventSoMeDeckParser.Parse(deck).Posts);
        var run = Assert.Single(post.Occurrences);

        Assert.Equal("x_v1_card.png", run.GraphicFileName);
        Assert.Equal(expected, run.SourcePhotoFileName);
    }

    [Fact]
    public void The_stale_summary_table_at_the_top_is_ignored()
    {
        // The table claims `..._v1_full.png` for the announcement; the post says `..._v1_card.png`,
        // and the post is what exists in the library. Two of the REAL file's rows are wrong this way.
        var announcement = Parsed().Posts.Single(p => p.Slug == "eldk27-announcement");

        Assert.DoesNotContain(
            announcement.Occurrences,
            o => o.GraphicFileName.Contains("_v1_full", StringComparison.Ordinal));
    }

    [Fact]
    public void The_body_is_the_post_text_without_the_production_notes_or_the_appendix()
    {
        var announcement = Parsed().Posts.Single(p => p.Slug == "eldk27-announcement");

        Assert.StartsWith("Welcome back from summer", announcement.Body, StringComparison.Ordinal);
        Assert.EndsWith("#ELDK27 #ExpertsLiveDK", announcement.Body, StringComparison.Ordinal);
        // The `graphics:` bullets are instructions to himself, not post content.
        Assert.DoesNotContain("Photo brief", announcement.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("**Style:**", announcement.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_appendix_after_the_last_post_is_not_swallowed_into_its_body()
    {
        var cola = Parsed().Posts.Single(p => p.Slug == "eldk27-coca-cola");

        Assert.DoesNotContain("How placement is validated", cola.Body, StringComparison.Ordinal);
        // ⚠️ The deck's own appendix still tells the reader to import `eldk27-social-calendar.csv`.
        // That CSV is retired (§824.8 Q4) and the slug-keyed markdown import replaced it — the
        // appendix must never reach a post body and read like an instruction.
        Assert.DoesNotContain("eldk27-social-calendar.csv", cola.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_slug_is_reported_and_skipped_rather_than_letting_file_order_decide()
    {
        var deck = Deck + """


## Post 99 — A second post claiming a used slug

`title:` Duplicate
`slug:` eldk27-networking
`dates:`

  - 2027-02-01 · `whatever.png` (X.JPG)

`graphics:`
- **Style:** framed card

Body text.

---
""";

        var result = EventSoMeDeckParser.Parse(deck);

        Assert.Equal(3, result.Posts.Count);
        Assert.Contains(result.Problems, p => p.Contains("eldk27-networking", StringComparison.Ordinal));
    }

    [Fact]
    public void A_post_with_no_slug_is_reported_and_does_not_stop_the_rest()
    {
        var deck = """
## Post 1 — No key

`title:` Nameless
`dates:`

  - 2026-08-04 · `a.png` (P.JPG)

`graphics:`
- **Style:** card

Body.

---

## Post 2 — Fine

`title:` Fine
`slug:` ok-slug
`dates:`

  - 2026-08-05 · `b.png` (Q.JPG)

`graphics:`
- **Style:** card

Body two.

---
""";

        var result = EventSoMeDeckParser.Parse(deck);

        Assert.Single(result.Posts);
        Assert.Equal("ok-slug", result.Posts[0].Slug);
        Assert.Contains(result.Problems, p => p.Contains("slug", StringComparison.OrdinalIgnoreCase));
    }
}
