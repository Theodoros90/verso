using Verso.Domain.Templates;

namespace Verso.UnitTests.Templates;

public sealed class DocumentCategoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StartsActive_WithTrimmedName()
    {
        var category = DocumentCategory.Create("  Loan documents  ", "elena", T0);

        Assert.Equal("Loan documents", category.Name);
        Assert.True(category.IsActive);
        Assert.Equal("elena", category.CreatedBy);
        Assert.Equal(T0, category.CreatedAt);
        Assert.NotEqual(Guid.Empty, category.Id);
    }

    [Fact]
    public void Deactivate_ThenActivate_TogglesIsActive()
    {
        var category = DocumentCategory.Create("Statements", "elena", T0);

        category.Deactivate();
        Assert.False(category.IsActive);

        category.Activate();
        Assert.True(category.IsActive);
    }

    [Fact]
    public void Template_SetCategory_StoresAndClears()
    {
        var template = Template.Create("INV-STD", "Commercial invoice", null, DocumentType.Invoice, TemplateContent.Empty, "elena", T0);
        var id = Guid.NewGuid();

        template.SetCategory(id);
        Assert.Equal(id, template.CategoryId);

        template.SetCategory(null);
        Assert.Null(template.CategoryId);
    }
}
