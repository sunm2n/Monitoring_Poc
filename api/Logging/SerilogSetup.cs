using Serilog;
using Serilog.Events;
using Serilog.Templates;

namespace PocApi.Logging;

/// <summary>
/// stdout 으로 나가는 JSON 의 모양을 여기서 전부 결정한다. docs/log-schema.md 와 1:1로 대응한다.
/// </summary>
public static class SerilogSetup
{
    /// <summary>
    /// docs/log-schema.md 의 문서 구조를 그대로 출력하는 템플릿.
    ///
    /// ★ AddJsonConsole() 이나 CompactJsonFormatter 를 그냥 쓰지 않는 이유:
    ///   - AddJsonConsole  → LogLevel, Category, State 같은 필드명이 나온다
    ///   - CompactJsonFormatter → @t, @mt, @l, @x 같은 축약 필드명이 나온다
    ///   둘 다 인덱스 필드명으로 쓰기에 부적절하고, 결국 Fluent Bit 에서 필드명을 하나하나
    ///   rename 하는 삽질로 이어진다. 처음부터 원하는 스키마로 뱉는 게 훨씬 싸다.
    ///
    /// ..rest() 는 위에서 명시적으로 쓰지 않은 나머지 프로퍼티 전부를 펼친다.
    /// 즉 company_id, user_id, trace_id, event, outcome, http, error, duration_ms,
    /// product_id ... 는 전부 여기로 흘러나온다. 필드가 늘어도 이 템플릿은 안 고쳐도 된다.
    /// </summary>
    private const string OutputTemplate =
        "{ {'@timestamp': UtcDateTime(@t), level: @l, message: @m, ..rest()} }\n";

    public static void Configure(HostBuilderContext context, LoggerConfiguration cfg)
    {
        cfg
            .MinimumLevel.Information()
            // EF Core 는 Information 레벨에서 실행되는 SQL 을 전부 뱉는다.
            // PoC 로그가 SQL 로 뒤덮이면 정작 봐야 할 도메인 이벤트가 묻힌다.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)

            // ── 등록 순서가 중요하다 ──────────────────────────────────────────
            // FromLogContext 가 먼저 와야 미들웨어가 넣은 company_id 가 들어가고,
            // 그 뒤의 WithProperty 기본값들은 AddPropertyIfAbsent 로 동작하므로 덮어쓰지 않는다.
            .Enrich.FromLogContext()

            // 예약 필드를 엉뚱한 타입으로 덮어쓴 프레임워크 로그를 먼저 걸러낸다.
            // 기본값 채우기(아래 WithProperty)보다 앞에 와야 걸러낸 자리를 다시 메울 수 있다.
            .Enrich.With(new SchemaGuardEnricher())

            .Enrich.WithProperty(LogSchema.Service, LogSchema.ServiceName)
            .Enrich.WithProperty(LogSchema.Env, LogSchema.EnvName)

            // ★ 함정 #8 방어.
            //   company_id 가 없는 문서는 DLS 에 걸려 회사 관리자에게 "영원히" 안 보인다.
            //   미들웨어가 닿지 않는 구간(기동 로그, 호스팅 로그)에도 최소한 unknown 을 박아둔다.
            .Enrich.WithProperty(LogSchema.CompanyId, LogSchema.UnknownCompany)
            .Enrich.WithProperty(LogSchema.UserId, LogSchema.AnonymousUser)
            .Enrich.WithProperty(LogSchema.Event, LogSchema.Events.Internal)

            .Enrich.With(new ErrorEnricher())
            .Enrich.With(new OutcomeEnricher())

            // 싱크는 Console 하나뿐이다. 이 앱이 로그에 대해 아는 것은 여기까지다.
            .WriteTo.Console(new ExpressionTemplate(OutputTemplate));
    }
}
