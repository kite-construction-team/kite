using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace Kite.TechEmpower.Plugin;

public struct WorldRow
{
    public int Id { get; set; }
    public int RandomNumber { get; set; }
}

public readonly record struct FortuneRow(int Id, string Message);

internal sealed class TechEmpowerDb
{
    public const string DefaultConnectionString =
        "Server=tfb-database;Database=hello_world;User Id=benchmarkdbuser;Password=benchmarkdbpass;SSL Mode=Disable;No Reset On Close=true;Maximum Pool Size=256;Enlist=false;Max Auto Prepare=16;Auto Prepare Min Usages=2;";

    private static readonly Comparison<FortuneRow> FortuneComparison =
        static (a, b) => string.CompareOrdinal(a.Message, b.Message);

    private readonly NpgsqlDataSource _dataSource;
    private WorldRow[] _cachedWorlds = [];
    private readonly SemaphoreSlim _cacheInitLock = new(1, 1);
    private volatile bool _cachePopulated;

    public TechEmpowerDb(string connectionString)
    {
        var builder = new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays();
        _dataSource = builder.Build();
    }

    public async Task<WorldRow> LoadSingleWorldAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = CreateReadCommand(conn, out NpgsqlParameter<int> idParam);
        idParam.TypedValue = Random.Shared.Next(1, 10001);
        return await ReadSingleRowAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<WorldRow[]> LoadMultipleWorldsAsync(int count, CancellationToken ct)
    {
        var results = new WorldRow[count];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn);
        for (int i = 0; i < count; i++)
        {
            var cmd = new NpgsqlBatchCommand("SELECT id, randomnumber FROM world WHERE id = $1");
            cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Random.Shared.Next(1, 10001) });
            batch.BatchCommands.Add(cmd);
        }

        await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int idx = 0;
        do
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results[idx] = new WorldRow { Id = reader.GetInt32(0), RandomNumber = reader.GetInt32(1) };
            }
            idx++;
        }
        while (idx < count && await reader.NextResultAsync(ct).ConfigureAwait(false));

        return results;
    }

    public async Task<WorldRow[]> UpdateMultipleWorldsAsync(int count, CancellationToken ct)
    {
        var results = new WorldRow[count];
        var ids = new int[count];
        var numbers = new int[count];

        for (int i = 0; i < count; i++)
        {
            ids[i] = Random.Shared.Next(1, 10001);
        }
        Array.Sort(ids);
        for (int i = 1; i < count; i++)
        {
            if (ids[i] <= ids[i - 1])
            {
                ids[i] = (ids[i - 1] % 10000) + 1;
            }
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var batch = new NpgsqlBatch(conn))
        {
            for (int i = 0; i < count; i++)
            {
                var cmd = new NpgsqlBatchCommand("SELECT id, randomnumber FROM world WHERE id = $1");
                cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = ids[i] });
                batch.BatchCommands.Add(cmd);
            }

            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
            int idx = 0;
            do
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    results[idx] = new WorldRow { Id = reader.GetInt32(0), RandomNumber = reader.GetInt32(1) };
                }
                idx++;
            }
            while (idx < count && await reader.NextResultAsync(ct).ConfigureAwait(false));
        }

        for (int i = 0; i < count; i++)
        {
            int nextNum = Random.Shared.Next(1, 10001);
            if (results[i].RandomNumber == nextNum)
            {
                nextNum = (nextNum % 10000) + 1;
            }
            results[i].RandomNumber = nextNum;
            numbers[i] = nextNum;
        }

        const string updateSql =
            "UPDATE world w SET randomnumber = u.new_val FROM (SELECT unnest($1) as id, unnest($2) as new_val) u WHERE w.id = u.id";

        await using var updateCmd = new NpgsqlCommand(updateSql, conn);
        updateCmd.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        updateCmd.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = numbers, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return results;
    }

    public async Task<List<FortuneRow>> LoadFortunesAsync(CancellationToken ct)
    {
        var result = new List<FortuneRow>();
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT id, message FROM fortune", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new FortuneRow(reader.GetInt32(0), reader.GetString(1)));
        }

        result.Add(new FortuneRow(0, "Additional fortune added at request time."));
        result.Sort(FortuneComparison);
        return result;
    }

    public ValueTask<WorldRow[]> LoadCachedWorldsAsync(int count, CancellationToken ct)
    {
        if (_cachePopulated)
        {
            return ValueTask.FromResult(ReadCachedSlice(count));
        }

        return LoadCachedWorldsSlowAsync(count, ct);
    }

    private WorldRow[] ReadCachedSlice(int count)
    {
        WorldRow[] cache = _cachedWorlds;
        var results = new WorldRow[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = cache[Random.Shared.Next(1, 10001)];
        }
        return results;
    }

    private async ValueTask<WorldRow[]> LoadCachedWorldsSlowAsync(int count, CancellationToken ct)
    {
        await _cacheInitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_cachePopulated)
            {
                var cache = new WorldRow[10001];
                await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("SELECT id, randomnumber FROM world", conn);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    int id = reader.GetInt32(0);
                    if ((uint)id < (uint)cache.Length)
                    {
                        cache[id] = new WorldRow { Id = id, RandomNumber = reader.GetInt32(1) };
                    }
                }
                _cachedWorlds = cache;
                _cachePopulated = true;
            }
        }
        finally
        {
            _cacheInitLock.Release();
        }

        return ReadCachedSlice(count);
    }

    private static NpgsqlCommand CreateReadCommand(NpgsqlConnection conn, out NpgsqlParameter<int> idParam)
    {
        var cmd = new NpgsqlCommand("SELECT id, randomnumber FROM world WHERE id = $1", conn);
        idParam = new NpgsqlParameter<int> { TypedValue = 1 };
        cmd.Parameters.Add(idParam);
        return cmd;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async Task<WorldRow> ReadSingleRowAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new WorldRow
        {
            Id = reader.GetInt32(0),
            RandomNumber = reader.GetInt32(1)
        };
    }
}
