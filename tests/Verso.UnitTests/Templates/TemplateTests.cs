using Verso.Domain;
using Verso.Domain.Templates;

namespace Verso.UnitTests.Templates;

public sealed class TemplateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static Template NewTemplate() =>
        Template.Create("INV-STD", "Commercial invoice", null, DocumentType.Invoice, TemplateContent.Empty, "elena", T0);

    private static TemplateContent ContentWith(string layoutMarker) =>
        TemplateContent.Empty with { Layout = $$"""{"layoutSchemaVersion":1,"marker":"{{layoutMarker}}"}""" };

    [Fact]
    public void Create_StartsWithOneDraft_AndNothingPublished()
    {
        var template = NewTemplate();

        var draft = Assert.Single(template.Versions);
        Assert.Same(draft, template.Draft);
        Assert.Equal(TemplateVersionStatus.Draft, draft.Status);
        Assert.Null(draft.VersionNumber);
        Assert.Null(template.LatestPublished);
    }

    [Fact]
    public void Publish_AssignsVersionNumbersInOrder()
    {
        var template = NewTemplate();

        var v1 = template.Publish("hash1", "nikos", T0.AddMinutes(1));
        template.StartDraft("elena", T0.AddMinutes(2));
        var v2 = template.Publish("hash2", "nikos", T0.AddMinutes(3));

        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Same(v2, template.LatestPublished);
        Assert.Null(template.Draft);
        Assert.Equal("nikos", v2.PublishedBy);
        Assert.Equal("hash2", v2.ContentHash);
    }

    [Fact]
    public void StartDraft_CopiesLatestPublishedContent()
    {
        var template = NewTemplate();
        template.SaveDraft(ContentWith("v1"), "elena", T0);
        template.Publish("hash1", "nikos", T0);

        var draft = template.StartDraft("elena", T0.AddMinutes(1));

        Assert.Equal(ContentWith("v1"), draft.Content);
        Assert.NotEqual(template.LatestPublished!.Id, draft.Id);
    }

    [Fact]
    public void StartDraft_IsRejected_WhenADraftAlreadyExists()
    {
        var template = NewTemplate();

        var ex = Assert.Throws<DomainRuleException>(() => template.StartDraft("elena", T0));
        Assert.Equal("template.draft_exists", ex.Code);
    }

    [Fact]
    public void SaveDraft_IsRejected_AfterPublishing_AndPublishedContentStaysUnchanged()
    {
        var template = NewTemplate();
        template.SaveDraft(ContentWith("v1"), "elena", T0);
        var v1 = template.Publish("hash1", "nikos", T0);

        var ex = Assert.Throws<DomainRuleException>(() => template.SaveDraft(ContentWith("changed"), "elena", T0));

        Assert.Equal("template.no_draft", ex.Code);
        Assert.Equal(ContentWith("v1"), v1.Content);
    }

    [Fact]
    public void Publish_IsRejected_WithoutADraft()
    {
        var template = NewTemplate();
        template.Publish("hash1", "nikos", T0);

        var ex = Assert.Throws<DomainRuleException>(() => template.Publish("hash2", "nikos", T0));
        Assert.Equal("template.no_draft", ex.Code);
    }
}
