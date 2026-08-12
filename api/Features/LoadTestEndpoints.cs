using PocApi.Logging;

namespace PocApi.Features;

/// <summary>
/// 대시보드에 그릴 데이터를 만들기 위한 로그 생성기.
///
/// 실제 DB 를 100번 두드리는 대신 로그만 생성한다. 목적이 "부하 테스트"가 아니라
/// "시각화할 만한 분포의 로그 확보"이기 때문이다. 이 엔드포인트는 PoC 전용이며
/// 실제 제품에는 존재하지 않는다.
/// </summary>
public static class LoadTestEndpoints
{
    private static readonly string[] Events =
    [
        LogSchema.Events.ProductList,
        LogSchema.Events.ProductGet,
        LogSchema.Events.ProductCreate,
        LogSchema.Events.ProductUpdate,
        LogSchema.Events.ProductDelete,
        LogSchema.Events.ProductPurchase,
    ];

    /// <summary>
    /// 예외를 한 번 던졌다 잡아서 실제 스택트레이스가 붙은 인스턴스를 만든다.
    ///
    /// <c>new Exception(...)</c> 만 하면 StackTrace 가 null 이라 error.stack_trace 가
    /// 예외 타입명 한 줄로 끝난다. FLS 데모에서 "숨겨서 안 보이는 것"과
    /// "원래 별 내용이 없던 것"을 구분할 수 없으면 시연이 설득력을 잃는다.
    /// </summary>
    private static T Thrown<T>(T exception) where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (T caught)
        {
            return caught;
        }
    }

    public static void MapLoadTestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/load-test", (int? count, HttpContext ctx) =>
        {
            var total = Math.Clamp(count ?? 100, 1, 1000);
            var rng = Random.Shared;

            var success = 0;
            var failure = 0;
            var fault = 0;

            for (var i = 0; i < total; i++)
            {
                var @event = Events[rng.Next(Events.Length)];
                var productId = rng.Next(1, 11);
                var quantity = rng.Next(1, 6);
                var duration = (long)rng.Next(3, 400);
                var roll = rng.NextDouble();

                var fields = new Dictionary<string, object?>
                {
                    [LogSchema.ProductId] = productId,
                    [LogSchema.DurationMs] = duration,
                };

                if (roll < 0.70)
                {
                    // 70% 성공
                    fields[LogSchema.Http] = AppLog.HttpFields("POST", $"/api/products/{productId}", 200);

                    if (@event == LogSchema.Events.ProductPurchase)
                    {
                        fields[LogSchema.Quantity] = quantity;
                        fields[LogSchema.Amount] = (long)(quantity * rng.Next(10_000, 500_000));
                    }

                    AppLog.Success(@event, "부하 생성 - 정상 처리", fields);
                    success++;
                }
                else if (roll < 0.95)
                {
                    // 25% 비즈니스 실패 (409 / 404)
                    var status = rng.Next(2) == 0 ? 409 : 404;
                    fields[LogSchema.Http] = AppLog.HttpFields("POST", $"/api/products/{productId}", status);
                    fields[LogSchema.Quantity] = quantity;

                    AppLog.Failure(
                        @event,
                        status == 409 ? "부하 생성 - 재고 부족" : "부하 생성 - 대상 없음",
                        fields,
                        status == 409 ? Thrown(new InsufficientStockException(quantity, 0)) : null);

                    failure++;
                }
                else
                {
                    // 5% 서버 장애 (500) — 스택트레이스가 들어 있는 문서를 확보한다
                    fields[LogSchema.Http] = AppLog.HttpFields("POST", $"/api/products/{productId}", 500);

                    AppLog.Fault(@event, "부하 생성 - 서버 오류", Thrown(new SimulatedDatabaseException()), fields);
                    fault++;
                }
            }

            AppLog.Success(
                LogSchema.Events.LoadTest,
                $"부하 생성을 완료했습니다 (총 {total}건)",
                new()
                {
                    ["generated_total"] = total,
                    ["generated_success"] = success,
                    ["generated_failure"] = failure,
                    ["generated_fault"] = fault,
                });

            return Results.Ok(new
            {
                company = ctx.CompanyId(),
                total,
                success,
                failure,
                fault,
            });
        });
    }
}
