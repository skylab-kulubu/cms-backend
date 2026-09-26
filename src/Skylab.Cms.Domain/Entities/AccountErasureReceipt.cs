using System.Text.Json.Nodes;

namespace Skylab.Cms.Domain.Entities;

/// <summary>
/// Proof that one Erasure command finished. It holds no subject and no
/// address, so it can be kept as the deletion record.
/// </summary>
public sealed class AccountErasureReceipt
{
    public Guid RequestId { get; private set; }
    public DateTime CompletedAt { get; private set; }
    public JsonObject Counts { get; private set; } = default!;

    private AccountErasureReceipt() { }

    public static AccountErasureReceipt Create(
        Guid requestId,
        DateTime completedAt,
        IReadOnlyDictionary<string, int> counts)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("Request id is required.", nameof(requestId));
        if (completedAt.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Completion time must be UTC.", nameof(completedAt));

        var json = new JsonObject();
        foreach (var (key, value) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(counts));
            json[key] = value;
        }

        return new AccountErasureReceipt
        {
            RequestId = requestId,
            // Postgres keeps microseconds; trimming here makes the first
            // response and every replay serialize the same instant.
            CompletedAt = new DateTime(completedAt.Ticks - completedAt.Ticks % 10, DateTimeKind.Utc),
            Counts = json
        };
    }

    public IReadOnlyDictionary<string, int> CountsByName() =>
        new SortedDictionary<string, int>(
            Counts.ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<int>()),
            StringComparer.Ordinal);
}
