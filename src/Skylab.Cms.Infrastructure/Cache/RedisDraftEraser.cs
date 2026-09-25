using StackExchange.Redis;

namespace Skylab.Cms.Infrastructure.Cache;

/// <summary>
/// Finds a subject's drafts by SCAN and deletes them. Draft keys carry the
/// subject in the middle (<c>draft:{clientId}:{sub}:{slug}</c>) or at the end
/// (<c>cd:{kind}:...:{sub}</c>), so they cannot be looked up by name.
/// </summary>
internal sealed class RedisDraftEraser(DraftRedisConnection connection)
{
    private const int PageSize = 500;

    public async Task<int> DeleteSubjectDraftsAsync(string subjectId, CancellationToken cancellationToken)
    {
        // The subject is interpolated into a glob; only a canonical UUID is
        // guaranteed to carry no glob metacharacters.
        if (!Guid.TryParseExact(subjectId, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), subjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Subject must be a canonical UUID.", nameof(subjectId));
        }

        var multiplexer = await connection.GetAsync();
        var database = multiplexer.GetDatabase();
        var deleted = 0L;
        foreach (var server in multiplexer.GetServers().Where(server => !server.IsReplica))
        {
            foreach (var pattern in Patterns(subjectId))
            {
                var batch = new List<RedisKey>(PageSize);
                await foreach (var key in server
                                   .KeysAsync(database.Database, pattern, PageSize)
                                   .WithCancellation(cancellationToken))
                {
                    batch.Add(key);
                    if (batch.Count < PageSize)
                        continue;

                    deleted += await database.KeyDeleteAsync(batch.ToArray());
                    batch.Clear();
                }

                if (batch.Count > 0)
                    deleted += await database.KeyDeleteAsync(batch.ToArray());
            }
        }

        return checked((int)deleted);
    }

    internal static string[] Patterns(string subjectId) =>
    [
        $"draft:*:{subjectId}:*",
        $"cd:*:{subjectId}"
    ];
}

/// <summary>
/// The draft Redis (<c>ConnectionStrings:Redis</c>) for commands the
/// distributed cache does not expose, such as SCAN. Connects on first use.
/// </summary>
internal sealed class DraftRedisConnection(string connectionString) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _multiplexer;

    public async Task<IConnectionMultiplexer> GetAsync()
    {
        if (_multiplexer is not null)
            return _multiplexer;

        await _gate.WaitAsync();
        try
        {
            if (_multiplexer is null)
            {
                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = false;
                // Keys name the subject; keep them out of exception messages.
                options.IncludeDetailInExceptions = false;
                options.ClientName ??= "cms-draft-eraser";
                _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
            }

            return _multiplexer;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
            await _multiplexer.DisposeAsync();
        _gate.Dispose();
    }
}
