using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SecureProxy.Api.Data;

/// <summary>
/// Used only by the EF Core command line tools ("dotnet ef migrations ..."). It lets them build the
/// model for SQL Server without starting the application or needing a real connection string.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ProxyDbContext>
{
    public ProxyDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ProxyDbContext>()
            .UseSqlServer("Server=design-time;Database=SecureProxy;Integrated Security=true")
            .Options);
}
