using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE C1: the template provider's resolution order is
/// <b>DB editor override → private config file → shipped default</b>. Each layer
/// wins over the one below it. Also covers <see cref="EmailTemplateProvider.RenderBodyFragment"/>
/// (body-only render for the portal welcome — no email shell, no Subject line).
/// </summary>
public sealed class EmailTemplateProviderPrivateConfigTests
{
    private const int EventId = 77;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-22T12:00:00Z");
    }

    private sealed class StubEmailContext : IEmailContextAccessor
    {
        private EmailContext? _ctx;
        public StubEmailContext(EmailContext? ctx) => _ctx = ctx;
        public EmailContext? Current => _ctx;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) { _ctx = c; return new D(); }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"tplprovider-{Guid.NewGuid():N}").Options);

    /// <summary>A fresh temp dir holding one template file.</summary>
    private static string DirWith(string key, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ceh-tpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + ".html"), content);
        return dir;
    }

    private static EmailTemplateProvider NewProvider(
        string shippedDir, string privateDir,
        IServiceScopeFactory? scopes = null, IEmailContextAccessor? ctx = null)
    {
        // The shipped dir must also hold a _layout.html for the renderer to load.
        if (!File.Exists(Path.Combine(shippedDir, "_layout.html")))
        {
            File.WriteAllText(Path.Combine(shippedDir, "_layout.html"),
                "Subject: {{subject}}\n<html><body>{{bodyContent}}</body></html>");
        }
        return new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = shippedDir,
                PrivateTemplateDirectory = privateDir,
            }),
            scopes, ctx);
    }

    [Fact]
    public void Private_config_file_wins_over_the_shipped_default()
    {
        var shipped = DirWith("welcome-speaker", "Subject: Shipped\n<p>shipped default</p>");
        var priv = DirWith("welcome-speaker", "Subject: Private\n<p>private edition copy</p>");

        var provider = NewProvider(shipped, priv);

        // GetDefaultText honours the private layer (per C1).
        Assert.Contains("private edition copy", provider.GetDefaultText("welcome-speaker"));
        Assert.DoesNotContain("shipped default", provider.GetDefaultText("welcome-speaker"));
    }

    [Fact]
    public void Shipped_default_is_used_when_no_private_file_exists()
    {
        var shipped = DirWith("welcome-volunteer", "Subject: Shipped\n<p>shipped default</p>");
        var priv = Path.Combine(Path.GetTempPath(), $"ceh-empty-{Guid.NewGuid():N}"); // no files

        var provider = NewProvider(shipped, priv);

        Assert.Contains("shipped default", provider.GetDefaultText("welcome-volunteer"));
    }

    [Fact]
    public async Task DB_override_wins_over_the_private_file()
    {
        var shipped = DirWith("welcome-sponsor", "Subject: Shipped\n<p>shipped default</p>");
        var priv = DirWith("welcome-sponsor", "Subject: Private\n<p>private edition copy</p>");

        using var db = NewDb();
        var store = new EmailTemplateOverrideStore(
            db, new MemoryCache(new MemoryCacheOptions()), new FixedClock());
        await store.UpsertAsync(EventId, "welcome-sponsor", "Subject: DB\n<p>db override wins</p>", "org@x");

        // Wire a real scope factory exposing the store, plus an ambient EmailContext
        // carrying the edition id so ResolveContent consults the DB override first.
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        var sp = services.BuildServiceProvider();
        var ctx = new StubEmailContext(new EmailContext("welcome-sponsor", EventId));

        var provider = NewProvider(shipped, priv,
            sp.GetRequiredService<IServiceScopeFactory>(), ctx);

        var rendered = provider.Render("welcome-sponsor", provider.NewTokenSet());
        Assert.Contains("db override wins", rendered.HtmlBody);
        Assert.DoesNotContain("private edition copy", rendered.HtmlBody);
        Assert.Equal("DB", rendered.Subject);
    }

    [Fact]
    public void RenderBodyFragment_strips_subject_and_layout()
    {
        var shipped = DirWith("welcome-speaker",
            "Subject: Hi {{firstName}}\n<p>Hello {{firstName}}, body here</p>");
        var priv = Path.Combine(Path.GetTempPath(), $"ceh-empty-{Guid.NewGuid():N}");

        var provider = NewProvider(shipped, priv);
        var tokens = provider.NewTokenSet();
        tokens["firstName"] = "Sam";

        var body = provider.RenderBodyFragment("welcome-speaker", tokens);

        Assert.Contains("Hello Sam, body here", body);
        Assert.DoesNotContain("Subject:", body);     // subject line stripped
        Assert.DoesNotContain("<html>", body);       // no email layout shell
        Assert.DoesNotContain("<body>", body);
    }
}
