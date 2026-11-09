namespace Verso.Domain;

/// <summary>
/// Thrown when an operation would break a domain invariant (e.g. editing a published version).
/// Application services check these rules first; this is the last line of defence.
/// </summary>
public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
