# 로그 스키마 명세 (v1)

> 이 문서가 PoC의 **실질적 산출물**이다. 인프라(OpenSearch / Fluent Bit)는 나중에 갈아엎을 수
> 있지만, 로그 스키마는 앱 코드 전체에 퍼지므로 여기서 확정해야 한다.
>
> 앱 · 수집기 · 인덱스 템플릿 · 권한 스크립트가 **모두 이 문서 하나를 참조**한다.
> 필드를 바꾸려면 이 문서를 먼저 고치고, 아래 "이 스키마를 참조하는 파일" 전부를 함께 고칠 것.

---

## 1. 표준 로그 문서

애플리케이션은 **stdout에 한 줄짜리 JSON**을 출력한다. 그 이상은 아무것도 하지 않는다.

```json
{
  "@timestamp": "2026-08-12T10:23:45.123Z",
  "level": "Information",
  "service": "poc-api",
  "env": "local",
  "company_id": "company1",
  "user_id": "user-001",
  "trace_id": "0af7651916cd43dd8448eb211c80319c",
  "event": "product.purchase",
  "outcome": "failure",
  "message": "재고가 부족하여 구매에 실패했습니다",
  "http": {
    "method": "POST",
    "path": "/api/products/12/purchase",
    "status_code": 409
  },
  "duration_ms": 34,
  "product_id": 12,
  "quantity": 5,
  "amount": 150000,
  "error": {
    "type": "InsufficientStockException",
    "message": "재고 부족 (요청 5, 보유 2)",
    "stack_trace": "at PocApi..."
  }
}
```

## 2. 필드 정의

| 필드 | 타입 (OpenSearch) | 필수 | 설명 |
|---|---|:--:|---|
| `@timestamp` | `date` | ✅ | UTC, ISO-8601 밀리초. 앱이 직접 찍는다 (수집 시각 아님) |
| `level` | `keyword` | ✅ | `Information` / `Warning` / `Error` |
| `service` | `keyword` | ✅ | 고정값 `poc-api` |
| `env` | `keyword` | ✅ | 고정값 `local` |
| `company_id` | `keyword` | ✅ | **DLS 기준 필드.** 없으면 회사 관리자에게 영원히 안 보인다 |
| `user_id` | `keyword` | ✅ | 미인증 시 `anonymous` |
| `trace_id` | `keyword` | ✅ | W3C trace id. 요청 단위 상관관계 추적 |
| `event` | `keyword` | ✅ | `<도메인>.<행위>` 형식. 대시보드 집계 축 |
| `outcome` | `keyword` | ✅ | `success` \| `failure` |
| `message` | `text` | ✅ | 사람이 읽는 한 줄 요약 |
| `http.method` | `keyword` | | |
| `http.path` | `keyword` | | 라우트 템플릿이 아니라 실제 경로 |
| `http.status_code` | `integer` | | |
| `duration_ms` | `long` | | 요청 처리 시간 |
| `product_id` | `integer` | | 도메인 필드 |
| `quantity` | `integer` | | 도메인 필드 |
| `amount` | `long` | | 도메인 필드 |
| `error.type` | `keyword` | | 예외 타입명 |
| `error.message` | `text` | | 예외 메시지 |
| `error.stack_trace` | `text` | | **FLS로 회사 관리자에게 숨기는 필드** |

### 프레임워크가 자동으로 붙이는 부가 필드

ASP.NET Core 가 요청 스코프에 넣는 값들이 그대로 따라 나온다. 스키마의 일부는 아니지만
실제 문서에 존재하므로 여기 기록해 둔다.

| 필드 | 예시 | 비고 |
|---|---|---|
| `RequestId` | `0HNNO523RDJTV:00000001` | 요청 식별자. `trace_id` 와 목적이 겹치지만 커넥션 단위로 더 촘촘하다 |
| `RequestPath` | `/api/products/1/purchase` | `http.path` 와 중복 |
| `ConnectionId` | `0HNNO523RDJTV` | Kestrel 커넥션 |
| `SourceContext` | `Microsoft.Hosting.Lifetime` | 프레임워크 로그에만 붙는다 |

인덱스 템플릿의 `dynamic_templates`가 이런 미정의 문자열 필드를 **`keyword`로** 떨어뜨린다.
`text` + `keyword` 멀티필드로 자동 매핑되는 기본 동작을 막아둔 것인데,
"템플릿에 반영하는 걸 깜빡한 필드가 조용히 DLS를 우회하는" 사고를 예방하기 위해서다.

**정리하려면** `SchemaGuardEnricher`에 제거 목록을 추가하면 된다. PoC에서는
"프레임워크가 뭘 끼워 넣는지"를 보여주는 편이 낫다고 판단해 남겨뒀다.

## 3. 설계 규칙 (어기면 PoC가 깨진다)

| 규칙 | 이유 |
|---|---|
| `company_id`는 **모든 로그에 필수** | DLS는 "필드가 없는 문서"를 매칭시키지 못한다. 미들웨어 바깥에서 찍히는 로그(기동 로그 등)도 최소 `unknown`을 넣는다 |
| `company_id`는 반드시 **keyword** | `text`로 자동 매핑되면 DLS의 `term` 쿼리가 매칭되지 않는다 (함정 #3) |
| `error.stack_trace`는 **별도 필드로 분리** | `message`에 섞여 있으면 FLS로 숨길 수 없다 |
| 개인정보(이름·연락처·상세 금액)는 **로그에 넣지 않는다** | 마스킹은 수집기가 아니라 앱에서. 개인정보 접속기록은 로그 시스템이 아니라 DB(`AuditLogs`)로 간다 |
| `event`는 `<도메인>.<행위>` | 집계 축. 메시지 문자열로만 남기면 집계가 불가능하다 |
| 스택트레이스는 **JSON 문자열 안에 개행 이스케이프**로 담는다 | 여러 줄로 쪼개지면 20줄짜리 예외가 로그 20건으로 색인된다 |

## 4. 이벤트 목록

| `event` | 발생 지점 | `outcome` | 비고 |
|---|---|---|---|
| `product.list` | `GET /api/products` | success | |
| `product.get` | `GET /api/products/{id}` | success / failure | 없는 ID → 404 |
| `product.create` | `POST /api/products` | success / failure | 유효성 실패 → 400 |
| `product.update` | `PUT /api/products/{id}` | success / failure | 없는 ID → 404 |
| `product.delete` | `DELETE /api/products/{id}` | success / failure | 이미 삭제 → 409 |
| `product.purchase` | `POST /api/products/{id}/purchase` | success / failure | 재고 부족 → 409, 강제 예외 → 500 |
| `http.request` | 모든 요청 (미들웨어) | success / failure | 액세스 로그 성격 |

## 5. 이 스키마를 참조하는 파일

스키마를 바꾸면 아래를 **전부 함께** 고쳐야 한다.

| 파일 | 역할 |
|---|---|
| [api/Logging/LogSchema.cs](../api/Logging/LogSchema.cs) | 필드명 상수 — 앱 코드는 문자열 리터럴을 쓰지 않는다 |
| [api/Logging/SerilogSetup.cs](../api/Logging/SerilogSetup.cs) | `ExpressionTemplate`로 위 JSON을 그대로 출력 |
| [fluent-bit/fluent-bit.conf](../fluent-bit/fluent-bit.conf) | JSON 파싱 · Docker 메타데이터 제거 |
| [opensearch/01-index-template.json](../opensearch/01-index-template.json) | 필드 타입 고정 (특히 `company_id: keyword`) |
| [opensearch/setup-security.sh](../opensearch/setup-security.sh) | DLS는 `company_id`, FLS는 `error.stack_trace`를 참조 |

## 6. 감사로그는 여기 없다 (의도적)

구매 이력 같은 **법적 보존 의무가 있는 기록**은 이 파이프라인을 타지 않는다.
MSSQL `AuditLogs` 테이블에 트랜잭션으로 적재한다. 근거:

- 로그 파이프라인은 **유실을 허용**하는 설계다 (`fluentd-async: true` — 수집기가 죽어도 앱은 계속 돈다)
- 유실을 허용하는 경로에 "누가 언제 무엇을 조회했는가"를 태우면 안 된다
- ISM 정책으로 N일 후 자동 삭제되는 저장소는 보존 의무와 충돌한다

→ 실증: [api/Features/PurchaseEndpoints.cs](../api/Features/PurchaseEndpoints.cs) 의 `AuditLogs` 적재 (Phase 7)
