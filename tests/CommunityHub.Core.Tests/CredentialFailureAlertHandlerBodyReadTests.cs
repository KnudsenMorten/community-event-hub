using System.Net;
using System.Text;
using CommunityHub.Core.Diagnostics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1113 — the credential handler must not destroy the response it was only supposed to watch.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-20 received an invoicing report reading *"Order 10764: The stream was
/// already consumed. It cannot be read again."* — an error about a dropped connection, rendered as
/// an unexplained internal fault against one customer's order.</para>
///
/// <para>🔑 <b>The handler reads the body INSIDE the pipeline</b>, where the content is still the
/// live network stream — <c>HttpClient</c> does its buffering after the chain returns. A read that
/// fails half way therefore leaves the content consumed and unbuffered, and the caller's own read
/// throws an <see cref="InvalidOperationException"/> that names neither the network nor the handler.
/// The old comment claimed the read was already buffered; that was true only on the happy path, and
/// the catch-all below it turned the unhappy one into someone else's problem.</para>
/// </remarks>
public sealed class CredentialFailureAlertHandlerBodyReadTests
{
    /// <summary>A response body whose stream throws part-way, like a reset connection.</summary>
    private sealed class FailingStream : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served > 0) throw new IOException("the connection was reset mid-body");
            buffer[offset] = (byte)'{';
            _served++;
            return 1;
        }
    }

    private sealed class StubInner : HttpMessageHandler
    {
        private readonly HttpContent _content;
        public StubInner(HttpContent content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content });
    }

    private static HttpClient Client(HttpContent content) =>
        new(new CredentialFailureAlertHandler("Test", () => null)
        {
            InnerHandler = new StubInner(content),
        });

    [Fact]
    public async Task A_body_read_that_dies_half_way_surfaces_the_TRANSPORT_fault_not_a_consumed_stream()
    {
        using var client = Client(new StreamContent(new FailingStream()));

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("https://x.test/anything"));

        // 🔒 The real cause, which the retry handler treats as transient and a human can act on.
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Contains("connection was reset", Flatten(ex), StringComparison.OrdinalIgnoreCase);

        // ⚰️ And explicitly NOT the message he was shown, which pointed at nothing.
        Assert.DoesNotContain("already consumed", Flatten(ex), StringComparison.OrdinalIgnoreCase);

        static string Flatten(Exception e) =>
            e.InnerException is null ? e.Message : e.Message + " | " + Flatten(e.InnerException);
    }

    [Fact]
    public async Task A_healthy_body_is_still_fully_readable_by_the_caller_afterwards()
    {
        // The handler reads every 200 looking for a hidden auth failure (§524), so "the caller can
        // still read it" is the property that keeps every integration working.
        using var client = Client(new StringContent(
            "{\"ok\":true,\"note\":\"nothing wrong here\"}", Encoding.UTF8, "application/json"));

        using var resp = await client.GetAsync("https://x.test/anything");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("nothing wrong here", body);
    }
}
