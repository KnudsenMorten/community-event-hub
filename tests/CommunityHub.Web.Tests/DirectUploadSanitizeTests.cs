using System.Reflection;
using CommunityHub.Uploads;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §494 — the direct-to-storage upload hands the BROWSER a pre-authenticated write URL, so the
/// server-side name derivation is load-bearing security, not cosmetics: it is the only thing
/// standing between a client-supplied string and the path that URL writes to.
///
/// <para>These pin the sanitizer against the shapes that would matter — path separators, traversal,
/// absolute paths, NTFS streams and URL-encoded separators — because a single surviving <c>/</c>
/// or <c>..</c> would let a caller aim the upload at a different folder.</para>
/// </summary>
public sealed class DirectUploadSanitizeTests
{
    private static string Sanitize(string? input) =>
        (string)typeof(DirectUploadEndpoints)
            .GetMethod("Sanitize", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { input })!;

    [Theory]
    [InlineData("../../secrets", "------secrets")]
    [InlineData("a/b/c", "a-b-c")]
    [InlineData("a\\b", "a-b")]
    [InlineData("/etc/passwd", "-etc-passwd")]
    [InlineData("C:file", "C-file")]
    [InlineData("file%2F..%2Fx", "file-2F---2Fx")]
    [InlineData("brochure:stream", "brochure-stream")]
    public void Structure_characters_never_survive(string input, string expected)
    {
        var got = Sanitize(input);
        Assert.Equal(expected, got);
        // The invariant that actually matters, stated independently of the expected string:
        Assert.DoesNotContain('/', got);
        Assert.DoesNotContain('\\', got);
        Assert.DoesNotContain(':', got);
        Assert.DoesNotContain("..", got);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Empty_or_structure_only_names_still_produce_a_usable_name(string? input)
    {
        // Must never return "" — an empty component would collapse the path and could target the
        // FOLDER itself rather than a file inside it.
        Assert.False(string.IsNullOrWhiteSpace(Sanitize(input)));
    }

    [Fact]
    public void Ordinary_names_are_left_readable()
    {
        // Sanitizing must not be so aggressive that a sponsor cannot recognise their own file.
        Assert.Equal("Fabrikam-Brochure_2027", Sanitize("Fabrikam Brochure_2027"));
    }
}
