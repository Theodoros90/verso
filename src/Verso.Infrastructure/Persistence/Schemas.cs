namespace Verso.Infrastructure.Persistence;

/// <summary>SQL schemas, one per module. Tables never reference another module's schema by foreign key.</summary>
public static class Schemas
{
    public const string Templates = "templates";
    public const string Workflows = "workflows";
    public const string Executions = "executions";
    public const string Documents = "documents";
    public const string Audit = "audit";

    /// <summary>Folder input (approved locations, scans, ingested files, access tests). Created by AddFolderInput.</summary>
    public const string Ingestion = "ingestion";

    /// <summary>Hangfire job storage (created by our migrations, not by Hangfire at startup).</summary>
    public const string Jobs = "hangfire";

    /// <summary>The schemas the InitialCreate migration creates. Later modules' schemas come with their own migration (<see cref="Ingestion"/>).</summary>
    public static readonly IReadOnlyList<string> All = [Templates, Workflows, Executions, Documents, Audit];
}
