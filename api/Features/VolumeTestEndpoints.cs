using System.Diagnostics;
using PocApi.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace PocApi.Features;

/// <summary>
/// 대용량 검증용 로그 생성기.
///
/// ── /api/load-test 와 뭐가 다른가 ────────────────────────────────────────────
/// load-test 는 "대시보드에 그릴 만한 분포의 로그"를 만드는 데모용이다.
/// 이쪽은 측정이 목적이라 세 가지가 다르다.
///
///   1. run_id + seq  — 앱이 만든 건수와 색인된 건수를 정확히 대조할 수 있다.
///                      fluentd-async: true 라서 버퍼가 넘치면 로그가 조용히 버려지는데,
///                      이 두 필드가 없으면 유실이 일어났다는 사실 자체를 알 수 없다.
///                      대용량 검증의 가장 중요한 산출물이 이 유실률이다.
///
///   2. ratePerSec    — 생성 속도를 제한할 수 있다. 무제한으로 쏟아붓고 "유실 90%" 를
///                      확인하는 것보다, "유실 없이 버티는 최대 속도"를 찾는 게 실전에 쓸모 있다.
///
///   3. daysBack      — 과거 시각으로 분산 생성한다. Fluent Bit 이 레코드의 @timestamp 로
///                      인덱스명을 만들기 때문에(Logstash_Format On) app-logs-YYYY.MM.DD 가
///                      여러 개 생긴다. 인덱스 수가 늘었을 때의 거동과 ISM 정책을
///                      실제로 볼 수 있다. 하루치 한 덩어리로는 관찰되지 않는 부분이다.
/// ──────────────────────────────────────────────────────────────────────────
///
/// PoC 전용이다. 실제 제품에 이런 엔드포인트를 두면 안 된다.
/// </summary>
public static class VolumeTestEndpoints
{
    private static readonly MessageTemplateParser TemplateParser = new();

    private static readonly string[] Events =
    [
        LogSchema.Events.ProductList,
        LogSchema.Events.ProductGet,
        LogSchema.Events.ProductCreate,
        LogSchema.Events.ProductUpdate,
        LogSchema.Events.ProductDelete,
        LogSchema.Events.ProductPurchase,
    ];

    public static void MapVolumeTestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/volume-test", (
            int? count,
            int? ratePerSec,
            int? daysBack,
            string? runId,
            HttpContext ctx) =>
        {
            var total = Math.Clamp(count ?? 10_000, 1, 50_000_000);
            var rate = Math.Max(ratePerSec ?? 0, 0);   // 0 = 무제한
            var spread = Math.Clamp(daysBack ?? 0, 0, 30);
            var run = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N")[..12] : runId;
            var company = ctx.CompanyId();

            var sw = Stopwatch.StartNew();
            var generated = Generate(total, rate, spread, run);
            sw.Stop();

            var elapsedSec = sw.Elapsed.TotalSeconds;

            // 이 로그 자체는 run_id 를 달지 않는다 — 대조 대상 건수에 섞이면 안 된다.
            AppLog.Success(
                LogSchema.Events.LoadTest,
                $"대용량 생성 완료: run={run} {generated}건 / {elapsedSec:F1}초",
                new()
                {
                    ["volume_run_id"] = run,
                    ["volume_generated"] = generated,
                });

            return Results.Ok(new
            {
                runId = run,
                company,
                requested = total,
                generated,
                elapsedSec = Math.Round(elapsedSec, 2),
                actualRatePerSec = elapsedSec > 0 ? (long)(generated / elapsedSec) : 0,
                daysBack = spread,
                rateLimit = rate == 0 ? "unlimited" : rate.ToString(),
            });
        });
    }

    private static long Generate(int total, int ratePerSec, int daysBack, string runId)
    {
        var rng = Random.Shared;
        var now = DateTimeOffset.UtcNow;
        long generated = 0;

        // 속도 제한용. 1000건 단위로 묶어서 검사한다 — 건당 검사는 그 자체가 병목이 된다.
        const int throttleChunk = 1000;
        var sw = Stopwatch.StartNew();

        for (var i = 1; i <= total; i++)
        {
            var timestamp = daysBack == 0
                ? now
                // 과거 daysBack 일에 균등 분산. 인덱스가 날짜별로 쪼개지는지 보기 위한 것.
                : now.AddSeconds(-rng.NextDouble() * daysBack * 86_400);

            var roll = rng.NextDouble();
            var (level, outcome, message, status) = roll switch
            {
                < 0.70 => (LogEventLevel.Information, LogSchema.OutcomeSuccess, "대용량 검증 - 정상 처리", 200),
                < 0.95 => (LogEventLevel.Warning, LogSchema.OutcomeFailure, "대용량 검증 - 비즈니스 실패", 409),
                _ => (LogEventLevel.Error, LogSchema.OutcomeFailure, "대용량 검증 - 서버 오류", 500),
            };

            var productId = rng.Next(1, 11);

            var properties = new List<LogEventProperty>
            {
                new(LogSchema.Event, new ScalarValue(Events[rng.Next(Events.Length)])),
                new(LogSchema.Outcome, new ScalarValue(outcome)),
                new(LogSchema.ProductId, new ScalarValue(productId)),
                new(LogSchema.Quantity, new ScalarValue(rng.Next(1, 6))),
                new(LogSchema.DurationMs, new ScalarValue((long)rng.Next(3, 400))),
                new(LogSchema.Http, new StructureValue(
                [
                    new LogEventProperty(LogSchema.HttpMethod, new ScalarValue("POST")),
                    new LogEventProperty(LogSchema.HttpPath, new ScalarValue($"/api/products/{productId}")),
                    new LogEventProperty(LogSchema.HttpStatusCode, new ScalarValue(status)),
                ])),

                // ★ 유실 대조용 두 필드
                new("run_id", new ScalarValue(runId)),
                new("seq", new ScalarValue((long)i)),
            };

            // 500 케이스에만 진짜 스택트레이스를 붙인다.
            // 전부 붙이면 문서당 크기가 부풀어 용량 측정이 왜곡된다.
            Exception? exception = status == 500 ? SharedFault : null;

            // 타임스탬프를 직접 지정하려면 LogEvent 를 만들어 Write 해야 한다.
            // 이 경로도 enricher 는 그대로 통과하므로 company_id / service / env 는 붙는다.
            var logEvent = new LogEvent(
                timestamp,
                level,
                exception,
                TemplateParser.Parse(message),
                properties);

            Log.Write(logEvent);
            generated++;

            if (ratePerSec > 0 && i % throttleChunk == 0)
            {
                var targetSec = (double)i / ratePerSec;
                var behindMs = (targetSec - sw.Elapsed.TotalSeconds) * 1000;

                if (behindMs > 1)
                {
                    Thread.Sleep((int)behindMs);
                }
            }
        }

        return generated;
    }

    /// <summary>
    /// 스택트레이스가 붙은 예외 인스턴스 하나를 재사용한다.
    /// 루프 안에서 매번 던졌다 잡으면 예외 처리 비용이 생성 속도를 지배해버려서,
    /// 측정하려는 것(파이프라인 처리량)이 아니라 .NET 예외 비용을 측정하게 된다.
    /// </summary>
    private static readonly Exception SharedFault = CreateFault();

    private static Exception CreateFault()
    {
        try
        {
            throw new SimulatedDatabaseException();
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
