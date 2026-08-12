using Microsoft.EntityFrameworkCore;
using PocApi.Data;
using PocApi.Features;
using PocApi.Logging;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 로깅에 대해 이 앱이 하는 일의 전부: stdout 에 정해진 스키마의 JSON 을 뱉는다.
// 어디로 실려가서 어디에 저장되는지는 앱의 관심사가 아니다.
builder.Host.UseSerilog(SerilogSetup.Configure);

var connectionString = builder.Configuration.GetConnectionString("PocDb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__PocDb 환경변수가 설정되지 않았습니다.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(
        maxRetryCount: 5,
        maxRetryDelay: TimeSpan.FromSeconds(5),
        errorNumbersToAdd: null)));

var app = builder.Build();

// 기동 로그. 미들웨어 바깥이라 company_id 는 enricher 기본값인 "unknown" 이 붙는다.
// (이 로그가 회사 관리자 계정에 보이지 않는 것 자체가 DLS 가 동작한다는 증거이기도 하다)
Log.Information("poc-api 를 기동합니다");

// MSSQL 은 기동에 20~40초가 걸린다(에뮬레이션 환경에서는 더). compose 의 healthcheck 로
// 이미 한 번 걸러지지만, 컨테이너 단독 실행 등을 대비해 앱에서도 한 번 더 기다린다. (함정 #6)
await WaitForDatabaseAsync(app.Services);

app.UseDefaultFiles();   // "/" → wwwroot/index.html
app.UseStaticFiles();

// company_id / user_id / trace_id 를 요청 수명 전체에 부착하는 미들웨어.
// 정적 파일보다 뒤, 엔드포인트보다 앞에 있어야 한다.
app.UseMiddleware<CompanyContextMiddleware>();

app.MapProductEndpoints();
app.MapPurchaseEndpoints();
app.MapAuditLogEndpoints();
app.MapLoadTestEndpoints();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

static async Task WaitForDatabaseAsync(IServiceProvider services)
{
    const int maxAttempts = 30;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            if (await db.Database.CanConnectAsync())
            {
                Log.Information("데이터베이스에 연결했습니다 (시도 {Attempt}회)", attempt);
                return;
            }
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            Log.Warning("데이터베이스 연결 대기 중 ({Attempt}/{Max}): {Reason}",
                attempt, maxAttempts, ex.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    throw new InvalidOperationException(
        $"데이터베이스에 연결하지 못했습니다 ({maxAttempts}회 시도). MSSQL 컨테이너 상태를 확인하세요.");
}
