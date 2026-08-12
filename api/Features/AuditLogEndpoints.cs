using Microsoft.EntityFrameworkCore;
using PocApi.Data;
using PocApi.Logging;

namespace PocApi.Features;

/// <summary>
/// ★ Phase 7 — "감사로그는 로그 시스템이 아니라 DB로 간다"의 실증.
///
/// 이 엔드포인트가 읽는 데이터는 OpenSearch 를 거치지 않는다. MSSQL AuditLogs 테이블이다.
/// Fluent Bit / OpenSearch 를 전부 내려도 이 화면은 정상 동작한다 — 그게 요점이다.
/// </summary>
public static class AuditLogEndpoints
{
    public static void MapAuditLogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audit-logs", async (HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();

            // 감사로그도 회사별로 격리된다. 다만 여기서는 OpenSearch DLS 가 아니라
            // 평범한 WHERE 절이 그 일을 한다 — 저장소가 다르면 격리 수단도 다르다.
            var items = await db.AuditLogs
                .Where(a => a.CompanyId == company)
                .OrderByDescending(a => a.AuditLogId)
                .Take(50)
                .Select(a => new
                {
                    a.AuditLogId,
                    a.CompanyId,
                    a.UserId,
                    a.Action,
                    a.TargetType,
                    a.TargetId,
                    a.Detail,
                    a.TraceId,
                    a.CreatedAt,
                })
                .ToListAsync();

            AppLog.Success(
                LogSchema.Events.AuditQuery,
                $"감사로그를 조회했습니다 ({items.Count}건, 저장소: MSSQL)");

            return Results.Ok(new
            {
                storage = "MSSQL AuditLogs 테이블 (OpenSearch 아님)",
                company,
                count = items.Count,
                items,
            });
        });
    }
}
