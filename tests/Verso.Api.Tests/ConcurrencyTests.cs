using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Domain.Templates;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class ConcurrencyTests(MigratedDatabaseFixture fixture)
{
    /// <summary>
    /// The service compares If-Match before saving, but two requests can both pass that check at the same time.
    /// The rowversion token makes the database reject the second write. This reproduces that race deterministically.
    /// </summary>
    [Fact]
    public async Task SecondWriterWithTheSameRowVersion_IsRejectedByTheDatabase()
    {
        var now = DateTimeOffset.UtcNow;
        var key = "C" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        Guid id;
        using (var setup = fixture.Factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<VersoDbContext>();
            var template = Template.Create(key, "Race", null, DocumentType.Invoice, TemplateContent.Empty, "test", now);
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            id = template.Id;
        }

        using var scopeA = fixture.Factory.Services.CreateScope();
        using var scopeB = fixture.Factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<VersoDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<VersoDbContext>();
        var a = await dbA.Templates.Include(t => t.Versions).SingleAsync(t => t.Id == id);
        var b = await dbB.Templates.Include(t => t.Versions).SingleAsync(t => t.Id == id);
        var seenByBoth = a.Draft!.RowVersion;

        a.SaveDraft(TemplateContent.Empty with { SampleData = """{"writer":"A"}""" }, "a", now);
        dbA.SetOriginalRowVersion(a.Draft!, seenByBoth);
        await dbA.SaveChangesAsync();

        b.SaveDraft(TemplateContent.Empty with { SampleData = """{"writer":"B"}""" }, "b", now);
        dbB.SetOriginalRowVersion(b.Draft!, seenByBoth);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        using var verify = fixture.Factory.Services.CreateScope();
        var stored = await verify.ServiceProvider.GetRequiredService<VersoDbContext>()
            .TemplateVersions.AsNoTracking().SingleAsync(v => v.TemplateId == id);
        Assert.Equal("""{"writer":"A"}""", stored.Content.SampleData);
    }

    [Fact]
    public async Task Database_RejectsASecondDraftForTheSameTemplate()
    {
        var now = DateTimeOffset.UtcNow;
        var key = "D" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        var template = Template.Create(key, "Two drafts", null, DocumentType.Invoice, TemplateContent.Empty, "test", now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        // Bypass the domain rule with raw SQL to prove the filtered unique index backs it up.
        const string emptyJson = "{}";
        var insert = db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO templates.TemplateVersions
                (Id, TemplateId, Status, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, Layout, DataSchema, SampleData,
                 PageSize, PageOrientation, MarginTopMm, MarginRightMm, MarginBottomMm, MarginLeftMm)
            VALUES ({Guid.NewGuid()}, {template.Id}, 'Draft', {now}, 'test', {now}, 'test', {emptyJson}, {emptyJson}, {emptyJson},
                 'A4', 'portrait', 18, 16, 18, 16)
            """);

        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => insert);
        Assert.Contains("UX_TemplateVersions_OneDraft", ex.Message, StringComparison.Ordinal);
    }
}
