namespace Verso.Infrastructure.Persistence;

/// <summary>SQL schemas, one per module. Tables never reference another module's schema by foreign key.</summary>
public static class Schemas
{
    public const string Templates = "templates";
    public const string Workflows = "workflows";
    public const string Executions = "executions";
    public const string Documents = "documents";
    public const string Audit = "audit";

    public static readonly IReadOnlyList<string> All = [Templates, Workflows, Executions, Documents, Audit];
}
