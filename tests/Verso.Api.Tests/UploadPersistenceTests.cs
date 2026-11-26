using Microsoft.EntityFrameworkCore;
using Verso.Domain.Ingestion;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

public sealed class UploadPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private readonly TestDatabase _database = new("uploads");

    public async Task InitializeAsync() => await _database.MigrateAsync();

    public async Task DisposeAsync() => await _database.DropAsync();

    private VersoDbContext NewContext() =>
        new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(_database.ConnectionString).Options);

    [Fact]
    public async Task Unique_workflow_and_hash_rejects_a_second_row()
    {
        var workflowId = Guid.NewGuid();
        var hash = new string('b', 64);
        await using (var first = NewContext())
        {
            first.Uploads.Add(Upload.Stage(workflowId, "a.pdf", ".pdf", "application/pdf", [1], hash, "synthetic.user", Now));
            await first.SaveChangesAsync();
        }

        await using var second = NewContext();
        second.Uploads.Add(Upload.Stage(workflowId, "b.pdf", ".pdf", "application/pdf", [1], hash, "synthetic.user", Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        // The same bytes for another workflow are fine.
        await using var third = NewContext();
        third.Uploads.Add(Upload.Stage(Guid.NewGuid(), "c.pdf", ".pdf", "application/pdf", [1], hash, "synthetic.user", Now));
        await third.SaveChangesAsync();
    }

    [Fact]
    public async Task Content_round_trips_a_5_MB_array()
    {
        var bytes = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        var id = Guid.Empty;
        await using (var write = NewContext())
        {
            var upload = Upload.Stage(Guid.NewGuid(), "big.pdf", ".pdf", "application/pdf", bytes, new string('c', 64), "synthetic.user", Now);
            id = upload.Id;
            write.Uploads.Add(upload);
            await write.SaveChangesAsync();
        }

        await using var read = NewContext();
        var loaded = await read.Uploads.SingleAsync(u => u.Id == id);
        Assert.Equal(bytes, loaded.Content);
        Assert.Equal(bytes.Length, loaded.Size);
        Assert.Equal(UploadStatus.Staged, loaded.Status);
    }
}
