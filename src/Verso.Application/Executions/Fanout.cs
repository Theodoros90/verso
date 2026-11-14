namespace Verso.Application.Executions;

/// <summary>The two paths of a Parallel branches step (ADR 0009). They run one after the other, A then B.</summary>
public static class Fanout
{
    public const string A = "branch-a";
    public const string B = "branch-b";

    /// <summary>The paths in the order they run.</summary>
    public static readonly IReadOnlyList<string> Paths = [A, B];
}
