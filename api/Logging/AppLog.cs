using Serilog;
using Serilog.Events;

namespace PocApi.Logging;

/// <summary>
/// 도메인 이벤트 로깅 헬퍼.
///
/// 엔드포인트가 <c>logger.LogInformation("상품 조회 성공")</c> 처럼 문자열만 남기면
/// "회사별 구매 실패율" 같은 집계가 불가능해진다. event + outcome 을 강제로 받게 해서
/// 집계 가능한 로그만 나가도록 만든다.
/// </summary>
public static class AppLog
{
    /// <summary>정상 처리. level=Information, outcome=success.</summary>
    public static void Success(string @event, string message, Dictionary<string, object?>? fields = null)
        => Write(LogEventLevel.Information, @event, LogSchema.OutcomeSuccess, message, null, fields);

    /// <summary>
    /// 예상된 실패(404 / 400 / 409 등). level=Warning, outcome=failure.
    /// 비즈니스 규칙에 따른 거절이지 장애가 아니므로 Error 가 아니다.
    /// 예외를 함께 넘기면 ErrorEnricher 가 error 객체를 채운다.
    /// </summary>
    public static void Failure(
        string @event,
        string message,
        Dictionary<string, object?>? fields = null,
        Exception? exception = null)
        => Write(LogEventLevel.Warning, @event, LogSchema.OutcomeFailure, message, exception, fields);

    /// <summary>
    /// 예상하지 못한 장애(500). level=Error, outcome=failure.
    /// ErrorEnricher 가 예외를 error.{type,message,stack_trace} 로 펼친다.
    /// </summary>
    public static void Fault(string @event, string message, Exception ex, Dictionary<string, object?>? fields = null)
        => Write(LogEventLevel.Error, @event, LogSchema.OutcomeFailure, message, ex, fields);

    private static void Write(
        LogEventLevel level,
        string @event,
        string outcome,
        string message,
        Exception? exception,
        Dictionary<string, object?>? fields)
    {
        var log = Log.ForContext(LogSchema.Event, @event)
                     .ForContext(LogSchema.Outcome, outcome);

        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                // destructureObjects: true —— http 처럼 Dictionary 로 넘어온 값을
                // "obj.ToString()" 스칼라가 아니라 중첩 JSON 객체로 펼치기 위해 필요하다.
                log = log.ForContext(key, value, destructureObjects: true);
            }
        }

        // 메시지 템플릿에 자리표시자를 두지 않는다.
        // 값은 전부 위의 ForContext 로 "이름 있는 필드"가 되고,
        // message 는 사람이 읽는 한 줄 요약이라는 역할만 갖는다.
        log.Write(level, exception, message);
    }

    /// <summary>중첩 http 객체를 만들기 위한 헬퍼. 익명 객체를 구조화 값으로 넘긴다.</summary>
    public static object HttpFields(string method, string path, int statusCode)
        => new Dictionary<string, object?>
        {
            [LogSchema.HttpMethod] = method,
            [LogSchema.HttpPath] = path,
            [LogSchema.HttpStatusCode] = statusCode,
        };
}
