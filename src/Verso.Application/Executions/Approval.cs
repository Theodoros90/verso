namespace Verso.Application.Executions;

/// <summary>
/// The Approval step: the run stops until a person holding the chosen role decides. Approved leaves by <c>approved</c>,
/// Declined by <c>rejected</c> (unconnected, a declined approval ends the run as Rejected). Escalation is recorded on the
/// request (a due time shown to approvers); nothing is sent or reassigned automatically.
/// </summary>
public static class Approval
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public const int MaxCommentLength = 500;

    public const string DefaultApprover = "operator";
    public const string DefaultEscalation = "48h";

    /// <summary>Who may decide: a key → (label, Verso role). Admins may always decide.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Role)> Approvers = new Dictionary<string, (string, string)>
    {
        ["operator"] = ("Operators", Roles.Operator),
        ["workflow-author"] = ("Workflow authors", Roles.WorkflowAuthor),
        ["template-author"] = ("Template authors", Roles.TemplateAuthor),
        ["auditor"] = ("Auditors", Roles.Auditor),
    };

    public static readonly IReadOnlyDictionary<string, (string Label, TimeSpan After)> Escalations = new Dictionary<string, (string, TimeSpan)>
    {
        ["24h"] = ("24 hours", TimeSpan.FromHours(24)),
        ["48h"] = ("48 hours", TimeSpan.FromHours(48)),
        ["5d"] = ("5 days", TimeSpan.FromDays(5)),
    };

    public static IReadOnlyList<string> ApproverLabels => [.. Approvers.Values.Select(a => a.Label)];

    public static IReadOnlyList<string> EscalationLabels => [.. Escalations.Values.Select(a => a.Label)];

    /// <summary>Whether this person may decide a request addressed to <paramref name="approver"/>.</summary>
    public static bool CanDecide(ICurrentUser user, string approver)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Roles.Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase)
            || (Approvers.TryGetValue(approver, out var entry) && user.Roles.Contains(entry.Role, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The approver and escalation settings of a node, falling back to the defaults.</summary>
    public static (string Approver, string Escalation) Settings(System.Text.Json.JsonElement config)
    {
        string Read(string name, string fallback) =>
            config.ValueKind == System.Text.Json.JsonValueKind.Object && config.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()!
                : fallback;
        return (Read("approver", DefaultApprover), Read("escalateAfter", DefaultEscalation));
    }
}
