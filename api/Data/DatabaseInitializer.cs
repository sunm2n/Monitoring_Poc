using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Serilog;

namespace PocApi.Data;

/// <summary>
/// db/init/01-seed.sql 을 실행해 스키마와 시드를 만든다.
///
/// ── 왜 별도의 init 컨테이너를 쓰지 않는가 (실측 근거) ──────────────────────
/// 정석은 mssql 이미지로 1회성 컨테이너를 띄워 sqlcmd 로 스크립트를 먹이는 것이다.
/// 실제로 그렇게 구성해봤고, 동작 자체는 한다. 문제는 비용이었다.
///
/// 이 환경(Apple Silicon + Docker Desktop 29)에서는 새 컨테이너가 실제로 start 되기까지
/// 수 분이 걸린다. 1초짜리 sqlcmd 한 방을 위해 2GB짜리 amd64 에뮬레이션 이미지를
/// 한 번 더 기동하는 셈이고, 그동안 api 는 depends_on 으로 묶여 계속 대기한다.
/// (실측: docker exec 로 같은 스크립트를 돌리면 3초 만에 끝난다)
///
/// API 는 어차피 DB 가 뜰 때까지 기다려야 하므로, 그 대기 구간에서 스크립트를 실행한다.
/// 스키마의 소유권은 여전히 SQL 파일에 있다 — 앱은 실행기일 뿐이고 EF 마이그레이션은 쓰지 않는다.
/// 컨테이너 기동이 빠른 x86 호스트라면 compose 에 mssql-init 서비스를 두는 쪽이 더 깔끔하다.
/// ──────────────────────────────────────────────────────────────────────────
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>sqlcmd 와 같은 규칙으로 GO 배치를 자른다 (한 줄이 통째로 GO 인 경우만).</summary>
    private static readonly Regex BatchSeparator = new(
        @"^\s*GO\s*;?\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task RunAsync(string connectionString, string? scriptPath)
    {
        // PocDb 는 아직 없을 수 있으므로 master 로 붙는다.
        var master = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        await WaitForServerAsync(master);

        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            Log.Warning("시드 스크립트를 찾지 못해 초기화를 건너뜁니다: {Path}", scriptPath ?? "(미설정)");
            return;
        }

        var script = await File.ReadAllTextAsync(scriptPath);
        var batches = BatchSeparator.Split(script)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();

        var executed = 0;

        foreach (var batch in batches)
        {
            // USE PocDb; 가 든 배치가 실행되면 이 커넥션의 컨텍스트가 바뀌고,
            // 이후 배치는 PocDb 안에서 실행된다. sqlcmd 와 동일한 동작이다.
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
            executed++;
        }

        Log.Information("데이터베이스 초기화를 완료했습니다 ({Count}개 배치 실행)", executed);
    }

    /// <summary>
    /// MSSQL 은 기동에 20~40초가 걸린다. 에뮬레이션 환경에서는 더 걸린다. (함정 #6)
    /// </summary>
    private static async Task WaitForServerAsync(string masterConnectionString)
    {
        const int maxAttempts = 60;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(masterConnectionString);
                await connection.OpenAsync();

                Log.Information("데이터베이스 서버에 연결했습니다 (시도 {Attempt}회)", attempt);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    Log.Warning("데이터베이스 기동 대기 중 ({Attempt}/{Max}): {Reason}",
                        attempt, maxAttempts, ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException(
            $"데이터베이스에 연결하지 못했습니다 ({maxAttempts}회 시도). MSSQL 컨테이너 상태를 확인하세요.");
    }
}
