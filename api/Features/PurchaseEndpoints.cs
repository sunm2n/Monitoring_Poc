using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PocApi.Data;
using PocApi.Logging;

namespace PocApi.Features;

/// <summary>재고 부족. 장애가 아니라 비즈니스 규칙에 의한 거절이다.</summary>
public sealed class InsufficientStockException(int requested, int available)
    : Exception($"재고 부족 (요청 {requested}, 보유 {available})")
{
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}

/// <summary>강제 장애 주입용. FLS 데모에 쓸 진짜 스택트레이스를 만들기 위해 존재한다.</summary>
public sealed class SimulatedDatabaseException()
    : Exception("결제 원장 기록 중 데이터베이스 연결이 끊어졌습니다 (PoC 강제 주입)");

public record PurchaseRequest(int Quantity);

public static class PurchaseEndpoints
{
    public static void MapPurchaseEndpoints(this WebApplication app)
    {
        // forceError=true 는 의도적인 장애 주입 경로다.
        // FLS(필드 레벨 보안) 데모를 하려면 error.stack_trace 가 실제로 들어 있는 문서가 있어야 한다.
        app.MapPost("/api/products/{id:int}/purchase",
            async (int id, PurchaseRequest req, bool? forceError, HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();
            var quantity = req.Quantity <= 0 ? 1 : req.Quantity;

            var product = await db.Products
                .FirstOrDefaultAsync(p => p.ProductId == id && p.CompanyId == company && !p.IsDeleted);

            if (product is null)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductPurchase,
                    "존재하지 않는 상품을 구매하려 했습니다",
                    new() { [LogSchema.ProductId] = id, [LogSchema.Quantity] = quantity });

                return Results.NotFound(new { error = "product_not_found", productId = id });
            }

            try
            {
                if (product.Stock < quantity)
                {
                    throw new InsufficientStockException(quantity, product.Stock);
                }

                if (forceError == true)
                {
                    // 재고 차감 직전에 터뜨린다 — 실제 장애와 같은 위치다.
                    throw new SimulatedDatabaseException();
                }

                var amount = product.Price * quantity;
                product.Stock -= quantity;

                db.Purchases.Add(new Purchase
                {
                    CompanyId = company,
                    ProductId = product.ProductId,
                    Quantity = quantity,
                    Amount = amount,
                });

                // ★ 감사로그는 로그 파이프라인이 아니라 DB 로 간다.
                //   구매와 같은 트랜잭션(SaveChanges 한 번)에 묶여 원자적으로 기록된다.
                //   OpenSearch 로 보내는 로그는 유실될 수 있지만, 이건 유실되지 않는다.
                db.AuditLogs.Add(new AuditLog
                {
                    CompanyId = company,
                    UserId = ctx.UserId(),
                    Action = "PURCHASE",
                    TargetType = "Product",
                    TargetId = product.ProductId.ToString(),
                    Detail = JsonSerializer.Serialize(new
                    {
                        productName = product.Name,
                        quantity,
                        amount,
                    }),
                    TraceId = ctx.TraceId(),
                });

                await db.SaveChangesAsync();

                AppLog.Success(
                    LogSchema.Events.ProductPurchase,
                    "상품을 구매했습니다",
                    new()
                    {
                        [LogSchema.ProductId] = product.ProductId,
                        [LogSchema.Quantity] = quantity,
                        // 금액은 집계용 숫자만 남긴다. 결제수단·구매자 정보 같은 개인정보는 로그에 넣지 않는다.
                        [LogSchema.Amount] = (long)amount,
                    });

                return Results.Ok(new
                {
                    productId = product.ProductId,
                    quantity,
                    amount,
                    remainingStock = product.Stock,
                });
            }
            catch (InsufficientStockException ex)
            {
                // 예상된 실패 → Warning. 예외를 함께 넘겨 error.type / error.message 를 채운다.
                AppLog.Failure(
                    LogSchema.Events.ProductPurchase,
                    "재고가 부족하여 구매에 실패했습니다",
                    new() { [LogSchema.ProductId] = id, [LogSchema.Quantity] = quantity },
                    ex);

                return Results.Conflict(new
                {
                    error = "insufficient_stock",
                    productId = id,
                    requested = ex.Requested,
                    available = ex.Available,
                });
            }
            catch (Exception ex)
            {
                // ⭐ 예상하지 못한 장애 → Error + 진짜 스택트레이스.
                //   이 문서가 FLS 검증의 대상이다:
                //     admin          → error.stack_trace 보임
                //     company1_admin → error.stack_trace 필드 자체가 없음
                AppLog.Fault(
                    LogSchema.Events.ProductPurchase,
                    "서버 오류로 구매에 실패했습니다",
                    ex,
                    new() { [LogSchema.ProductId] = id, [LogSchema.Quantity] = quantity });

                return Results.Json(
                    new { error = "internal_server_error", productId = id, traceId = ctx.TraceId() },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}
