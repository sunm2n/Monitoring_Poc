using System.Diagnostics;
using Serilog.Context;

namespace PocApi.Logging;

/// <summary>
/// 요청 하나의 수명 동안 company_id / user_id / trace_id 를 LogContext 에 밀어 넣는다.
///
/// ★ 이 미들웨어 하나로 이후 모든 로그에 company_id 가 자동으로 붙는다.
///   엔드포인트에서 일일이 넣을 필요가 없고, 넣는 걸 깜빡할 수도 없다.
///   "깜빡할 수 없다"가 중요하다 — company_id 가 빠진 문서는 DLS 때문에 회사 관리자에게
///   영원히 보이지 않는데, 그 사실은 아무 에러도 없이 조용히 발생한다.
///
/// 이 PoC는 인증을 구현하지 않으므로 헤더에서 꺼낸다.
/// 실제 제품에서는 이 두 줄만 JWT 클레임에서 꺼내도록 바꾸면 되고, 나머지는 그대로다.
/// </summary>
public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    private const string CompanyHeader = "X-Company-Id";
    private const string UserHeader = "X-User-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var companyId = Header(ctx, CompanyHeader, LogSchema.UnknownCompany);
        var userId = Header(ctx, UserHeader, LogSchema.AnonymousUser);
        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

        ctx.Items[LogSchema.CompanyId] = companyId;
        ctx.Items[LogSchema.UserId] = userId;
        ctx.Items[LogSchema.TraceId] = traceId;

        using (LogContext.PushProperty(LogSchema.CompanyId, companyId))
        using (LogContext.PushProperty(LogSchema.UserId, userId))
        using (LogContext.PushProperty(LogSchema.TraceId, traceId))
        {
            // 정적 파일(index.html 등)까지 액세스 로그를 남기면 데모 로그가 잡음으로 덮인다.
            var isApi = ctx.Request.Path.StartsWithSegments("/api");
            var sw = Stopwatch.StartNew();

            try
            {
                await next(ctx);
            }
            catch (Exception ex)
            {
                // 엔드포인트가 잡지 못한 예외의 마지막 그물.
                sw.Stop();
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.Clear();
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }

                AppLog.Fault(
                    LogSchema.Events.HttpRequest,
                    "처리되지 않은 예외로 요청이 실패했습니다",
                    ex,
                    RequestFields(ctx, sw));

                if (!ctx.Response.HasStarted)
                {
                    await ctx.Response.WriteAsJsonAsync(new { error = "internal_server_error", traceId });
                }

                return;
            }

            sw.Stop();

            if (isApi)
            {
                var fields = RequestFields(ctx, sw);
                var message = $"{ctx.Request.Method} {ctx.Request.Path} → {ctx.Response.StatusCode}";

                if (ctx.Response.StatusCode >= 400)
                {
                    AppLog.Failure(LogSchema.Events.HttpRequest, message, fields);
                }
                else
                {
                    AppLog.Success(LogSchema.Events.HttpRequest, message, fields);
                }
            }
        }
    }

    private static Dictionary<string, object?> RequestFields(HttpContext ctx, Stopwatch sw) => new()
    {
        [LogSchema.Http] = AppLog.HttpFields(
            ctx.Request.Method,
            ctx.Request.Path.Value ?? string.Empty,
            ctx.Response.StatusCode),
        [LogSchema.DurationMs] = sw.ElapsedMilliseconds,
    };

    private static string Header(HttpContext ctx, string name, string fallback)
    {
        var value = ctx.Request.Headers[name].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

/// <summary>엔드포인트에서 현재 요청의 회사/사용자/추적 값을 꺼내기 위한 헬퍼.</summary>
public static class HttpContextExtensions
{
    public static string CompanyId(this HttpContext ctx)
        => ctx.Items[LogSchema.CompanyId] as string ?? LogSchema.UnknownCompany;

    public static string UserId(this HttpContext ctx)
        => ctx.Items[LogSchema.UserId] as string ?? LogSchema.AnonymousUser;

    public static string TraceId(this HttpContext ctx)
        => ctx.Items[LogSchema.TraceId] as string ?? string.Empty;
}
