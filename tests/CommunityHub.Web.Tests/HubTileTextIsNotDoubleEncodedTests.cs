using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1047 — A HUB TILE'S TEXT IS A C# STRING, SO AN HTML ENTITY IN IT IS ENCODED TWICE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, with a screenshot of the Marketing / SoMe hub: the §1042 card read
/// <b>"Post Editor — walk &amp;amp; approve"</b>. The title carried the entity <c>&amp;amp;</c>;
/// <c>_HubGrid.cshtml</c> renders it as <c>@t.Title</c>, which HTML-encodes — so the ampersand of the
/// entity was itself encoded and the organizer read the markup.</para>
///
/// <para>🔑 <b>Why this is worth a test rather than a one-character fix.</b> The failure is
/// invisible at the place the mistake is made. In a <c>.cshtml</c> file, surrounded by markup where
/// <c>&amp;amp;</c> is correct, the literal looks right; it only goes wrong because these particular
/// strings are C# VALUES that Razor will encode later. The neighbouring hub
/// (<c>/Organizer/Logistics</c> — "Speaker &amp; order review", "Party RSVPs &amp; headcount") has
/// always used a plain ampersand, so the codebase already disagreed with itself and nothing said so.
/// Anyone writing the next tile has a 50/50 chance of copying the wrong one.</para>
///
/// <para>⚠️ Deliberately NOT limited to <c>&amp;amp;</c>. <c>&amp;mdash;</c>, <c>&amp;rarr;</c>,
/// <c>&amp;hellip;</c> and <c>&amp;nbsp;</c> fail identically, and the em-dash tiles elsewhere prove
/// the temptation is real — the fix is to type the CHARACTER (—, →, …), which these files already do
/// everywhere else.</para>
/// </remarks>
public sealed class HubTileTextIsNotDoubleEncodedTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// One hub-tile construction, captured whole: <c>new("/Organizer/X", "Title", "Description")</c>
    /// and the <c>new HubTile(…)</c> spelling. Stops at the closing paren before the next tile so a
    /// match covers exactly one tile's arguments.
    /// </summary>
    private static readonly Regex TileConstruction = new(
        @"new(?:\s+HubTile)?\(\s*""/[A-Za-z0-9_/]+""[^;]*?\)\s*,?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Any HTML character entity — named (&amp;amp;) or numeric (&amp;#8212;).</summary>
    private static readonly Regex HtmlEntity = new(
        @"&(?:[a-zA-Z][a-zA-Z0-9]{1,10}|#[0-9]{1,6}|#[xX][0-9a-fA-F]{1,5});",
        RegexOptions.Compiled);

    [Fact]
    public void No_hub_tile_title_or_description_contains_an_html_entity()
    {
        var pages = Directory.GetFiles(PagesDir(), "*.cshtml", SearchOption.AllDirectories);
        var failures = new List<string>();

        foreach (var page in pages)
        {
            var razor = File.ReadAllText(page);

            foreach (Match tile in TileConstruction.Matches(razor))
            {
                // Only the STRING LITERALS in the construction. Razor comments and surrounding
                // markup legitimately contain entities and must not be judged here — the comment
                // added beside the §1047 fix says "&amp;" in prose, explaining the very bug.
                foreach (Match literal in Regex.Matches(tile.Value, "\"([^\"]*)\""))
                {
                    var text = literal.Groups[1].Value;
                    var entity = HtmlEntity.Match(text);
                    if (!entity.Success) continue;

                    failures.Add(
                        $"{Path.GetFileName(page)}: hub tile text contains the HTML entity "
                        + $"'{entity.Value}' — \"{text}\"");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "A hub tile's title/description is a C# string rendered as `@t.Title` / `@t.Description`, "
            + "which Razor HTML-ENCODES. An entity here is encoded a SECOND time and the organizer "
            + "reads the markup on the card (§1047: \"Post Editor — walk &amp; approve\"). Type the "
            + "CHARACTER instead — & — as /Organizer/Logistics has always done.\n\n  "
            + string.Join("\n  ", failures));
    }

    /// <summary>
    /// 🔒 The test must be able to FAIL. A regex that silently matches no tiles would pass for ever
    /// and look like a guarantee — §1041's lesson, where a green test proved nothing because it
    /// never saw the case it was meant to catch.
    /// </summary>
    [Fact]
    public void The_scan_actually_finds_hub_tiles()
    {
        var pages = Directory.GetFiles(PagesDir(), "*.cshtml", SearchOption.AllDirectories);
        var tiles = pages.Sum(p => TileConstruction.Matches(File.ReadAllText(p)).Count);

        // The seven organizer hubs alone carry dozens; a handful would mean the pattern has drifted.
        Assert.True(tiles > 20, $"Only {tiles} hub tiles matched — the pattern has drifted.");
    }
}
