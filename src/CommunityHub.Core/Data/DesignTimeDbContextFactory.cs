using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CommunityHub.Core.Data;

/// <summary>
/// Design-time factory used ONLY by the EF Core tools (e.g.
/// <c>dotnet ef migrations add</c>). It is never used at runtime — the web/jobs
/// hosts register <see cref="CommunityHubDbContext"/> through DI with the real
/// connection string. The placeholder connection string here is enough for the
/// tooling to build the model and scaffold migrations; it is never connected to.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommunityHubDbContext>
{
    public CommunityHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseSqlServer("Server=(localdb)\\design-time;Database=CommunityHub;Trusted_Connection=True;")
            .Options;
        return new CommunityHubDbContext(options);
    }
}
