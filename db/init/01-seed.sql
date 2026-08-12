-- =================================================================
-- PocDb 초기화 / 시드 스크립트
--
-- 실행 방식: sqlcmd (MSSQL 2022 컨테이너 진입점에서 배치 단위로 실행)
-- 멱등성  : 여러 번 실행해도 에러 없이 같은 최종 상태가 되어야 한다.
--           (IF NOT EXISTS 가드 + 시드 데이터 존재 여부 체크)
--
-- 왜 이 파일이 필요한가 (docs/log-schema.md 6절 참조):
--   OpenSearch 로그 파이프라인은 "유실을 허용"하는 설계다.
--   (fluent-bit 가 죽어도 앱은 계속 응답해야 하므로 async 전송)
--   개인정보 접속기록/구매이력처럼 법적 보존 의무가 있는 데이터를
--   유실 허용 경로에 태우면 안 된다. 그래서 AuditLogs 는 로그가 아니라
--   MSSQL 에 "트랜잭션"으로 적재한다 — 이 스크립트는 그 주장을 실증하기
--   위한 스키마다.
-- =================================================================

-- -----------------------------------------------------------------
-- 1. 데이터베이스 생성 (없으면)
-- -----------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'PocDb')
BEGIN
    PRINT 'Creating database PocDb...';
    CREATE DATABASE PocDb;
END
GO

-- CREATE DATABASE 는 자체 배치여야 하고, 이후 배치부터 USE 가 반영된다.
-- (주의: GO 이후 USE PocDb 를 반드시 넣을 것 — 안 넣으면 이후 오브젝트가
--  master 에 생성되어 조용히 실패하거나 엉뚱한 DB를 오염시킨다.)
USE PocDb;
GO

-- -----------------------------------------------------------------
-- 2. 테이블 생성 (없으면)
-- -----------------------------------------------------------------

-- 회사(테넌트) 마스터
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Companies')
BEGIN
    CREATE TABLE Companies
    (
        CompanyId VARCHAR(50)   NOT NULL PRIMARY KEY,
        Name      NVARCHAR(100) NOT NULL
    );
END
GO

-- 상품
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Products')
BEGIN
    CREATE TABLE Products
    (
        ProductId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyId VARCHAR(50)   NOT NULL,
        Name      NVARCHAR(200) NOT NULL,
        Price     DECIMAL(18,2) NOT NULL,
        Stock     INT           NOT NULL,
        IsDeleted BIT           NOT NULL DEFAULT 0,
        CONSTRAINT FK_Products_Companies
            FOREIGN KEY (CompanyId) REFERENCES Companies(CompanyId)
    );
END
GO

-- 구매 이력
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Purchases')
BEGIN
    CREATE TABLE Purchases
    (
        PurchaseId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyId  VARCHAR(50)   NOT NULL,
        ProductId  INT           NOT NULL,
        Quantity   INT           NOT NULL,
        Amount     DECIMAL(18,2) NOT NULL,
        CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Purchases_Products
            FOREIGN KEY (ProductId) REFERENCES Products(ProductId)
    );
END
GO

-- 감사로그(AuditLogs)
-- ─────────────────────────────────────────────────────────────
-- 의도적으로 로그 파이프라인(OpenSearch/Fluent Bit)이 아니라 여기,
-- MSSQL 에 둔다. "누가 언제 무엇을 조회/구매했는가"는 법적 보존
-- 의무가 있는 기록이므로:
--   1) 유실을 허용하는 비동기 로그 파이프라인을 타면 안 되고
--   2) ISM 정책으로 N일 후 자동 삭제되는 저장소에 두면 보존 의무와
--      충돌한다.
-- 따라서 구매/삭제 등 민감 행위는 API 코드에서 DB 트랜잭션으로
-- AuditLogs 에 적재한다 (api/Features/PurchaseEndpoints.cs 참조).
-- ─────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AuditLogs')
BEGIN
    CREATE TABLE AuditLogs
    (
        AuditLogId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyId  VARCHAR(50)   NOT NULL,
        UserId     VARCHAR(100)  NOT NULL,
        Action     VARCHAR(100)  NOT NULL,
        TargetType VARCHAR(50)   NOT NULL,
        TargetId   VARCHAR(100)  NULL,
        Detail     NVARCHAR(1000) NULL,
        TraceId    VARCHAR(64)   NULL,
        CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- -----------------------------------------------------------------
-- 3. 조회 성능용 인덱스 (없으면)
-- -----------------------------------------------------------------

-- 상품 목록 조회 시 회사 + 삭제여부로 자주 필터링됨
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Products_CompanyId_IsDeleted' AND object_id = OBJECT_ID('Products')
)
BEGIN
    CREATE INDEX IX_Products_CompanyId_IsDeleted
        ON Products (CompanyId, IsDeleted);
END
GO

-- 감사로그는 회사별 + 최신순 조회가 기본 패턴
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_AuditLogs_CompanyId_CreatedAt' AND object_id = OBJECT_ID('AuditLogs')
)
BEGIN
    CREATE INDEX IX_AuditLogs_CompanyId_CreatedAt
        ON AuditLogs (CompanyId, CreatedAt);
END
GO

-- -----------------------------------------------------------------
-- 4. 시드 데이터 (Companies 가 비어 있을 때만 삽입 -> 재실행해도 중복 안 됨)
-- -----------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM Companies)
BEGIN
    PRINT 'Seeding Companies...';

    INSERT INTO Companies (CompanyId, Name) VALUES
        ('company1', N'가나다 상사'),
        ('company2', N'마바사 유통');

    PRINT 'Seeding Products...';

    -- company1: 가나다 상사 (5개 상품, 그 중 1개는 재고부족 시나리오용 Stock=1)
    INSERT INTO Products (CompanyId, Name, Price, Stock, IsDeleted) VALUES
        ('company1', N'무선 마우스',        25000.00,  80, 0),
        ('company1', N'기계식 키보드',       89000.00,  45, 0),
        ('company1', N'27인치 모니터',      329000.00,  20, 0),
        ('company1', N'노트북 거치대',       32000.00,   1, 0), -- 재고부족 시나리오용
        ('company1', N'USB-C 허브',          45000.00,  60, 0);

    -- company2: 마바사 유통 (5개 상품, 그 중 1개는 재고부족 시나리오용 Stock=1)
    INSERT INTO Products (CompanyId, Name, Price, Stock, IsDeleted) VALUES
        ('company2', N'사무용 의자',        189000.00,  25, 0),
        ('company2', N'전동 스탠딩 데스크',  459000.00,  30, 0),
        ('company2', N'A4 복사용지 (2500매)', 38000.00, 100, 0),
        ('company2', N'레이저 프린터',       259000.00,   1, 0), -- 재고부족 시나리오용
        ('company2', N'사무용 데스크탑 PC', 890000.00,  22, 0);
END
GO

-- -----------------------------------------------------------------
-- 5. 시드 결과 확인용 SELECT (sqlcmd 실행 로그에서 눈으로 확인)
-- -----------------------------------------------------------------
SELECT
    (SELECT COUNT(*) FROM Companies) AS CompanyCount,
    (SELECT COUNT(*) FROM Products)  AS ProductCount;
GO
