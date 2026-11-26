using Verso.Domain;
using Verso.Domain.Ingestion;

namespace Verso.UnitTests.Ingestion;

public sealed class UploadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static Upload NewUpload() =>
        Upload.Stage(Guid.NewGuid(), "invoice.pdf", ".pdf", "application/pdf", [1, 2, 3], new string('a', 64), "synthetic.user", Now);

    [Fact]
    public void Stage_sets_status_staged_and_keeps_content()
    {
        var workflowId = Guid.NewGuid();
        var upload = Upload.Stage(workflowId, "invoice.pdf", ".pdf", "application/pdf", [1, 2, 3], new string('a', 64), "synthetic.user", Now);

        Assert.NotEqual(Guid.Empty, upload.Id);
        Assert.Equal(workflowId, upload.WorkflowId);
        Assert.Equal(UploadStatus.Staged, upload.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, upload.Content);
        Assert.Equal(3, upload.Size);
        Assert.Equal(".pdf", upload.Extension);
        Assert.Equal("synthetic.user", upload.UploadedBy);
        Assert.Equal(Now, upload.CreatedAt);
        Assert.Null(upload.ExecutionId);
    }

    [Fact]
    public void MarkTaken_clears_content_and_is_idempotent()
    {
        var upload = NewUpload();
        var executionId = Guid.NewGuid();
        upload.AttachExecution(executionId);

        upload.MarkTaken();
        upload.MarkTaken();

        Assert.Equal(UploadStatus.Taken, upload.Status);
        Assert.Null(upload.Content);
        Assert.Equal(executionId, upload.ExecutionId);
        Assert.Equal(3, upload.Size);
    }

    [Fact]
    public void Expire_only_from_staged()
    {
        var upload = NewUpload();

        upload.Expire();

        Assert.Equal(UploadStatus.Expired, upload.Status);
        Assert.Null(upload.Content);
    }

    [Fact]
    public void Taken_cannot_expire()
    {
        var upload = NewUpload();
        upload.MarkTaken();

        Assert.Throws<DomainRuleException>(upload.Expire);
        Assert.Equal(UploadStatus.Taken, upload.Status);
    }
}
