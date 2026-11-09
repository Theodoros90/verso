using Microsoft.EntityFrameworkCore;

namespace Verso.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for Verso. Each module keeps its tables in its own SQL schema
/// (see <see cref="Schemas"/>). Entity configurations are added per module from M2 (template storage) onwards.
/// </summary>
public sealed class VersoDbContext(DbContextOptions<VersoDbContext> options) : DbContext(options);
