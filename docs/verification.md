# 검증 결과

실행일: **2026-08-12**
환경: macOS (Apple Silicon, arm64) / Docker Desktop 29.6.2 / Docker VM 메모리 7.75GB

```bash
./scripts/verify.sh --seed      # 데모 데이터 생성 후 검증
./scripts/verify.sh --restart   # api 컨테이너를 실제로 재생성한 뒤 검증
```

**최종 결과: 10개 항목 전부 통과 (10 / 0)**

---

## 주장 1 — 컨테이너가 죽어도 로그는 남는다

`docker compose rm -sf api && docker compose up -d api` 로 컨테이너를 **완전히 제거 후 재생성**했다.

| 측정 | 값 |
|---|---|
| 재생성 후 api 컨테이너 기동 시각 | `2026-08-12T04:16:33.239Z` |
| OpenSearch 에 남아 있는 가장 오래된 로그 | `2026-08-12T04:16:07.516Z` |
| `docker compose logs api` 로 볼 수 있는 가장 오래된 로그 | `2026-08-12T04:16:33.448Z` |

✅ 컨테이너보다 **26초 앞선 로그**가 OpenSearch에 그대로 남아 있다.
✅ 같은 시각의 로그를 컨테이너 쪽에서는 이미 볼 수 없다.

> **주의 — 흔한 오해 하나를 정정한다.**
> "fluentd 로깅 드라이버를 쓰면 `docker logs` 가 아예 안 된다"는 말이 돌아다니는데, 사실이 아니다.
> Docker 20.10부터 **dual logging** 이라 해서 원격 드라이버를 쓸 때도 로컬에 링버퍼 캐시를 함께 남긴다.
> 다만 그 캐시는 **컨테이너에 붙어 있어서 컨테이너가 사라지면 같이 사라진다.**
> 위 표의 세 번째 줄이 재생성 시각과 같은 이유가 그것이다.

### 회의 자료용 캡처 포인트

두 화면을 나란히 놓는다.

1. `docker compose logs api | head -1` → 재생성 시각 이후 로그만 존재
2. Dashboards Discover에서 같은 시간대 조회 → 재생성 이전 로그가 그대로 존재

---

## 주장 2 — 회사별 로그 격리가 무료 기능으로 가능하다

| 조회 주체 | 조건 | 결과 |
|---|---|---|
| `admin` | `company_id: company1` | **108건** |
| `admin` | `company_id: company2` | **108건** |
| `company1_admin` | 조건 없이 전체 검색 | **108건** (= company1 전량) |
| `company1_admin` | `company_id: company2` 를 **직접 지정** | **0건** ⭐ |

✅ 회사 관리자가 검색 조건을 직접 조작해도 다른 회사 문서는 한 건도 나오지 않는다.

이 차단은 애플리케이션이 아니라 **저장소(OpenSearch Security 플러그인)** 가 한다.
`_search`가 아니라 `_count` API를 직접 두드려도 결과는 같다.

### 어떻게 동작하는가

```json
PUT _plugins/_security/api/roles/company1_reader
{
  "index_permissions": [{
    "index_patterns": ["app-logs-*"],
    "dls": "{\"term\": {\"company_id\": \"company1\"}}",
    ...
  }]
}
```

`dls` 값이 **JSON 객체가 아니라 JSON을 담은 문자열**이라는 점이 최대 함정이다.
따옴표 이스케이프를 틀리면 API가 조용히 받아들이고 필터는 걸리지 않는다.
검증은 등록 후 되읽어서 `json.loads` 로 두 번 파싱해보는 것이 확실하다.

```bash
curl -sk -u admin:$PW https://localhost:9200/_plugins/_security/api/roles/company1_reader
# → "dls": "{\"term\": {\"company_id\": \"company1\"}}"
```

### 전제 조건

`company_id` 가 반드시 **`keyword`** 여야 한다. `text` 로 자동 매핑되면 DLS의 `term` 쿼리가
아무것도 매칭하지 못하고, **에러 없이 전 건이 보이거나 전 건이 안 보인다.**

```bash
curl -sk -u admin:$PW https://localhost:9200/_index_template/app-logs-template
# → "company_id": {"type": "keyword"}
```

이래서 compose가 `os-setup` **완료 후에** `fluent-bit`을 띄운다.
첫 문서가 템플릿보다 먼저 들어가면 이 조건이 깨진다.

---

## 주장 3 — 민감 필드를 역할별로 숨길 수 있다 (FLS)

**같은 문서** (`app-logs-2026.08.12/55wv9J8B0LBCUpthSYX8`) 를 두 계정으로 조회한 결과다.

### `admin` 이 보는 것

```json
{
  "@timestamp": "2026-08-12T04:17:16.2819068Z",
  "level": "Error",
  "message": "서버 오류로 구매에 실패했습니다",
  "event": "product.purchase",
  "outcome": "failure",
  "company_id": "company1",
  "user_id": "user-001",
  "trace_id": "65c291f97cd2fe684feacb0ef83d22e5",
  "product_id": 1,
  "quantity": 1,
  "error": {
    "type": "SimulatedDatabaseException",
    "message": "결제 원장 기록 중 데이터베이스 연결이 끊어졌습니다 (PoC 강제 주입)",
    "stack_trace": "PocApi.Features.SimulatedDatabaseException: 결제 원장 기록 중 ...\n   at PocApi.Features.PurchaseEndpoints...MoveNext() in /src/Features/PurchaseEndpoints.cs:line 57"
  }
}
```

### `company1_admin` 이 보는 것

```json
{
  "@timestamp": "2026-08-12T04:17:16.2819068Z",
  "level": "Error",
  "message": "서버 오류로 구매에 실패했습니다",
  "event": "product.purchase",
  "outcome": "failure",
  "company_id": "company1",
  "user_id": "user-001",
  "trace_id": "65c291f97cd2fe684feacb0ef83d22e5",
  "product_id": 1,
  "quantity": 1,
  "error": {
    "type": "SimulatedDatabaseException",
    "message": "결제 원장 기록 중 데이터베이스 연결이 끊어졌습니다 (PoC 강제 주입)"
  }
}
```

✅ `error.stack_trace` **필드 자체가 없다.** 마스킹된 값도, 빈 문자열도 아니다.
✅ 나머지 필드는 완전히 동일하다 — 회사 관리자도 장애 사실과 원인 요약은 볼 수 있다.

설정은 역할 한 줄이다. `~` 접두사가 제외를 뜻한다.

```json
"fls": ["~error.stack_trace"]
```

> 이게 성립하려면 스택트레이스가 `message` 와 **분리된 독립 필드**여야 한다.
> 텍스트 로그에서처럼 메시지 뒤에 이어 붙였다면 숨길 방법이 없다.
> 로그 스키마를 먼저 확정해야 하는 실질적인 이유가 이것이다.

---

## 주장 4 — 앱은 로그 백엔드를 몰라도 된다

세 가지 서로 다른 방식으로 확인했다.

| 검사 | 결과 |
|---|---|
| `PocApi.csproj` 의 `PackageReference` | `Serilog.AspNetCore`, `Serilog.Expressions`, `Microsoft.EntityFrameworkCore.SqlServer` — **검색엔진 패키지 없음** |
| 소스의 `using` 선언 | OpenSearch / Elasticsearch / Nest **없음** |
| 빌드 산출물 `/app` 의 어셈블리 | 관련 DLL **하나도 없음** |

앱이 로그에 대해 하는 일의 전부는 이것이다.

```csharp
.WriteTo.Console(new ExpressionTemplate(
    "{ {'@timestamp': UtcDateTime(@t), level: @l, message: @m, ..rest()} }\n"));
```

수집은 컨테이너 **바깥**에서 Docker 로깅 드라이버가 처리한다.
K8s에서 DaemonSet 수집기가 하는 일과 같은 구조이고, 백엔드를 Loki로 바꿔도
`api/` 디렉터리는 한 글자도 바뀌지 않는다.

> `grep -ri opensearch api/` 는 **결과가 나온다.** 전부 "왜 이렇게 설계했는가"를 적은 주석이다.
> 의존성 유무는 위 표의 세 가지로 판정하는 것이 맞다.

---

## Phase 7 — 감사로그 분리 실증

구매 이력은 로그 파이프라인을 타지 않고 MSSQL `AuditLogs` 테이블로 간다.

```
$ curl -H "X-Company-Id: company1" http://localhost:8080/api/audit-logs

storage : MSSQL AuditLogs 테이블 (OpenSearch 아님)
company : company1
count   : 1
  - PURCHASE Product 1 {"productName":"무선 마우스","quantity":2,"amount":50000.00} | trace dc98d6a1f9...

$ curl -H "X-Company-Id: company2" http://localhost:8080/api/audit-logs
건수: 0
```

✅ 회사별 격리가 되지만, 여기서는 DLS가 아니라 **평범한 `WHERE` 절**이 그 일을 한다.
✅ Fluent Bit / OpenSearch를 전부 내려도 이 조회는 정상 동작한다.

### 왜 나눴는가

| | 로그 파이프라인 | 감사로그 (DB) |
|---|---|---|
| 유실 허용 | **예** (`fluentd-async: true` — 수집기가 죽어도 앱은 계속 돈다) | 아니오 (구매와 같은 트랜잭션) |
| 보존 기간 | ISM 정책으로 7일 후 자동 삭제 | 법적 보존 의무를 따름 |
| 목적 | 운영·디버깅·집계 | "누가 언제 무엇을 했는가"의 증거 |

유실을 허용하도록 설계된 경로에 개인정보 접속기록을 태울 수는 없다.
`ISM` 으로 자동 삭제되는 저장소는 보존 의무와 정면으로 충돌한다.

---

## 대시보드 만들기 (회의 데모용)

`admin` 계정 / **Global** 테넌트에서 `app-logs-*` 인덱스 패턴(`@timestamp`)을 만든 뒤:

| 시각화 | 설정 |
|---|---|
| 회사별 로그량 추이 | Date histogram(`@timestamp`) × `company_id` split |
| 이벤트별 성공/실패 비율 | `event` terms × `outcome` split (누적 막대) |
| 에러 발생 Top 5 | `error.type` terms |
| 평균 응답시간 | `duration_ms` avg × `http.path` |

> `event` + `outcome` 두 필드만 있으면 이 대시보드가 전부 나온다.
> 로그를 메시지 문자열로만 남겼다면 어느 것도 만들 수 없다.

회사 계정으로 로그인해 같은 대시보드를 만들면 **자기 회사 데이터만** 그려진다.
DLS가 쿼리 레벨에서 걸리므로 시각화 코드는 아무것도 바꿀 필요가 없다.

---

## 실측한 함정 기록

계획 단계에서 예상한 것과 실제로 시간을 잡아먹은 곳이 달랐다.

| # | 함정 | 증상 | 해결 | 예상했나 |
|---|---|---|---|:--:|
| 1 | **`InvariantGlobalization=true`** | `Microsoft.Data.SqlClient` 가 ICU를 요구해 DB 연결이 전부 실패. 증상은 "DB 기동 대기 무한 반복"으로 나타나 원인이 안 보인다 | csproj 에서 `false` | ❌ |
| 2 | **예약 필드 타입 충돌** | ASP.NET Core 기동 경고가 `http` 프로퍼티에 문자열 `"8080"` 을 담는데 템플릿에서 `http` 는 객체. `mapper_parsing_exception` → **벌크 청크 전체 재시도 → 정상 문서까지 중복 색인 → 재시도 소진 후 청크 폐기** | `SchemaGuardEnricher` 로 앱에서 타입 강제 | ❌ |
| 3 | **컨테이너 기동이 극도로 느림** | 이 머신에서 새 컨테이너가 실제로 start 되기까지 **수 분**. `docker compose up` 이 멈춘 것처럼 보인다 | 인내. 별도 init 컨테이너를 없애 기동 수를 줄임 | ❌ |
| 4 | **MSSQL 이미지가 amd64 전용** | Apple Silicon 에서 에뮬레이션 필수 | `platform: linux/amd64` + Rosetta | ✅ |
| 5 | **스택트레이스가 한 줄뿐** | `new Exception(...)` 만 하면 `StackTrace` 가 null 이라 FLS 시연에서 "숨긴 것"과 "원래 없던 것"이 구분 안 됨 | 한 번 던졌다 잡아서 인스턴스 생성 | ❌ |
| 6 | DLS JSON 이스케이프 | 틀려도 API가 조용히 받는다 | 따옴표 없는 heredoc + 되읽어 이중 파싱 검증 | ✅ |
| 7 | `company_id` 가 `text` 로 매핑 | DLS 가 침묵 속에 무력화 | 템플릿을 첫 문서보다 먼저 등록 (compose 의존성) | ✅ |
| 8 | ISM `rollover` 액션 | alias 없이 날짜별 인덱스를 쓰면 rollover 가 영구 실패하고, ISM 은 실패 시 자동 전환이 없어 **삭제 단계로 영원히 못 넘어간다** | rollover 액션 제거. 날짜별 인덱스 자체가 롤오버 역할 | ❌ |

### 소요 시간 실측

| Phase | 계획 | 실제 | 비고 |
|---|---|---|---|
| 0~1 OpenSearch + Dashboards | 1.5h | **~0.3h** | 이미지 pull 이 대부분 |
| 2 MSSQL + 시드 | 1h | **~0.7h** | 컨테이너 기동 대기가 대부분 |
| 3 API + 로깅 + 화면 | 4h | **~1.5h** | `ExpressionTemplate` 은 한 번에 맞았다 |
| 4 Fluent Bit | 3h | **~0.3h** | 파서 설정이 처음부터 정확했다 |
| 5 템플릿 + 권한 | 4h | **~0.5h** | 스크립트로 만들어 둔 덕분 |
| 6 검증 | 2h | **~1.5h** | 함정 #1, #2 디버깅이 여기 포함 |

> **계획 대비 크게 줄었지만, 줄어든 이유가 중요하다.**
> 함정 대부분이 사전 조사로 미리 걸러졌고(파서 설정·DLS 이스케이프·인덱스 템플릿 선등록),
> 실제로 시간을 먹은 것은 **예상 목록에 없던 것들**(#1, #2, #3)이었다.
> 실제 구축 일정을 산정할 때는 "알려진 함정 회피 시간"보다 **"모르는 함정 디버깅 시간"** 에
> 버퍼를 둬야 한다는 뜻이다.

---

## 이 PoC로 답이 나오지 않는 것

- **성능·용량 산정** — 단일 노드 + 216건 로그로는 아무것도 알 수 없다
- **HA 구성** — 노드 장애 시 동작 미확인
- **온프렘 고객사 서버 사양** — 단일 노드가 버티는 한계 미측정
- **로그 보존 비용** — 스토리지 증가 추이 실측 필요
- **Keycloak 연동 시 역할 자동 생성** — 고객사 추가 프로세스 미설계
- **감사로그 위변조 방지** — WORM 또는 해시 체인 미검토
