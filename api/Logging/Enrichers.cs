using Serilog.Core;
using Serilog.Events;

namespace PocApi.Logging;

/// <summary>
/// 예외를 <c>error</c> 중첩 객체로 변환한다.
///
/// 이게 왜 필요한가:
///   Serilog 는 예외를 LogEvent.Exception 에 따로 담아두고, 프로퍼티로는 넣지 않는다.
///   그런데 우리는 스택트레이스를 <c>error.stack_trace</c> 라는 "독립된 필드"로 만들어야
///   OpenSearch FLS 로 그 필드만 숨길 수 있다. 메시지 문자열에 섞여 들어가면 숨길 방법이 없다.
///
///   엔드포인트마다 손으로 error 객체를 만들게 하면 언젠가 빠뜨린다.
///   여기서 한 번 처리하면 어떤 경로로 예외가 로깅되든 스키마가 보장된다.
/// </summary>
public sealed class ErrorEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Exception is null)
        {
            return;
        }

        var ex = logEvent.Exception;

        // ex.ToString() 은 내부 예외와 스택프레임을 개행으로 이어붙인 문자열이다.
        // JSON 문자열 값으로 들어가면서 개행이 \n 으로 이스케이프되므로,
        // 20줄짜리 예외가 로그 20건으로 쪼개지지 않고 문서 1건 안에 온전히 담긴다.
        // (텍스트 로깅 대비 JSON 로깅의 가장 실용적인 이득)
        var error = new StructureValue(
        [
            new LogEventProperty(LogSchema.ErrorType, new ScalarValue(ex.GetType().Name)),
            new LogEventProperty(LogSchema.ErrorMessage, new ScalarValue(ex.Message)),
            new LogEventProperty(LogSchema.ErrorStackTrace, new ScalarValue(ex.ToString())),
        ]);

        logEvent.AddPropertyIfAbsent(new LogEventProperty(LogSchema.Error, error));
    }
}

/// <summary>
/// <c>outcome</c> 이 명시되지 않은 로그(프레임워크 로그, 기동 로그 등)에 기본값을 채운다.
/// Error 이상은 failure, 그 외는 success.
///
/// AddPropertyIfAbsent 를 쓰므로 도메인 코드가 명시한 값이 항상 이긴다.
/// 기본값을 채우는 이유는 대시보드에서 outcome 이 없는 문서가 집계에서 통째로 빠지는 걸 막기 위함이다.
/// </summary>
public sealed class OutcomeEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var outcome = logEvent.Level >= LogEventLevel.Error
            ? LogSchema.OutcomeFailure
            : LogSchema.OutcomeSuccess;

        logEvent.AddPropertyIfAbsent(new LogEventProperty(LogSchema.Outcome, new ScalarValue(outcome)));
    }
}
