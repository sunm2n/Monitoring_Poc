namespace PocApi.Logging;

/// <summary>
/// 로그 필드명 상수. 앱 코드는 로그 필드명을 문자열 리터럴로 쓰지 않는다.
///
/// 필드 하나를 바꾸면 인덱스 템플릿(company_id 는 keyword여야 한다)과
/// 권한 스크립트(DLS 는 company_id, FLS 는 error.stack_trace 를 참조한다)가 같이 깨진다.
/// 그래서 이름을 한 곳에 모아두고, 바꿀 때 무엇이 함께 깨지는지 docs/log-schema.md 5절에 적어뒀다.
/// </summary>
public static class LogSchema
{
    // ── 공통 필드 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 로그 한 건의 고유 ID. 수집기가 이 값을 문서 _id 로 쓴다.
    ///
    /// ★ 왜 필요한가 (대용량 검증에서 실측된 사고):
    ///   Fluent Bit 은 벌크 청크 단위로 재시도한다. 그런데 연결 수준 실패
    ///   (http_do=-1 — 응답을 못 받은 경우)에는 서버가 이미 처리했는지 알 수 없다.
    ///   실제로 OpenSearch 쪽 index_failed=0 / rejected=0 인데도, 즉 색인은 다 성공했는데도
    ///   응답만 유실되어 청크가 재시도됐고, 문서 ID 가 자동 생성이라 전량이 두 번 색인됐다.
    ///   (5,000건 생성 → 9,210건 색인, 서로 다른 seq 는 4,973개)
    ///
    ///   이 필드를 수집기의 Id_Key 로 지정하면 재시도가 덮어쓰기(upsert)가 되어 멱등해진다.
    ///   로그 시스템은 at-least-once 전달이므로, 중복 제거 수단이 없으면 대용량에서 반드시 샌다.
    /// </summary>
    public const string LogId = "log_id";

    public const string Level = "level";
    public const string Service = "service";
    public const string Env = "env";

    /// <summary>DLS(문서 레벨 보안) 기준 필드. 이 값이 없는 문서는 회사 관리자에게 영원히 안 보인다.</summary>
    public const string CompanyId = "company_id";

    public const string UserId = "user_id";
    public const string TraceId = "trace_id";

    /// <summary>&lt;도메인&gt;.&lt;행위&gt; 형식. 대시보드 집계 축.</summary>
    public const string Event = "event";

    /// <summary>success | failure. event 와 이 필드의 조합이 대시보드의 전부다.</summary>
    public const string Outcome = "outcome";

    // ── HTTP (중첩 객체) ─────────────────────────────────────────────────────
    public const string Http = "http";
    public const string HttpMethod = "method";
    public const string HttpPath = "path";
    public const string HttpStatusCode = "status_code";

    public const string DurationMs = "duration_ms";

    // ── 도메인 필드 ──────────────────────────────────────────────────────────
    public const string ProductId = "product_id";
    public const string Quantity = "quantity";
    public const string Amount = "amount";

    // ── 오류 (중첩 객체) ─────────────────────────────────────────────────────
    public const string Error = "error";
    public const string ErrorType = "type";
    public const string ErrorMessage = "message";

    /// <summary>
    /// FLS(필드 레벨 보안)로 회사 관리자에게 숨기는 필드.
    /// message 와 반드시 분리돼 있어야 한다 — 섞여 있으면 숨길 방법이 없다.
    /// </summary>
    public const string ErrorStackTrace = "stack_trace";

    // ── 값 ───────────────────────────────────────────────────────────────────
    public const string OutcomeSuccess = "success";
    public const string OutcomeFailure = "failure";

    public const string ServiceName = "poc-api";
    public const string EnvName = "local";

    /// <summary>미들웨어 바깥(기동 로그 등)에서 찍히는 로그의 기본 회사값.</summary>
    public const string UnknownCompany = "unknown";

    public const string AnonymousUser = "anonymous";

    // ── 이벤트명 ─────────────────────────────────────────────────────────────
    public static class Events
    {
        public const string ProductList = "product.list";
        public const string ProductGet = "product.get";
        public const string ProductCreate = "product.create";
        public const string ProductUpdate = "product.update";
        public const string ProductDelete = "product.delete";
        public const string ProductPurchase = "product.purchase";
        public const string AuditQuery = "audit.query";
        public const string LoadTest = "loadtest.run";

        /// <summary>모든 요청에 대한 액세스 로그.</summary>
        public const string HttpRequest = "http.request";

        /// <summary>프레임워크/기동 로그 등 도메인 이벤트가 아닌 것들의 기본값.</summary>
        public const string Internal = "app.internal";
    }
}
