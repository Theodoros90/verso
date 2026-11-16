namespace Verso.Application.Documents;

/// <summary>
/// The archive step's "Keep for" value: <c>permanent</c>, or an amount and a unit, <c>d</c> (days), <c>m</c> (months) or
/// <c>y</c> (years), for example <c>90d</c>, <c>6m</c>, <c>7y</c>.
/// </summary>
public static class RetentionPeriod
{
    /// <summary>The date the document is kept until, or null for a permanent record or a value that isn't recognised.</summary>
    public static DateTimeOffset? Until(string retention, DateTimeOffset from)
    {
        if (retention.Length < 2 || !int.TryParse(retention.AsSpan(0, retention.Length - 1), out var amount) || amount < 1)
        {
            return null;
        }

        return retention[^1] switch
        {
            'd' => from.AddDays(amount),
            'm' => from.AddMonths(amount),
            'y' => from.AddYears(amount),
            _ => null,
        };
    }
}
