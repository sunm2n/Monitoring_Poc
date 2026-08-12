using Microsoft.EntityFrameworkCore;

namespace PocApi.Data;

public sealed class Company
{
    public string CompanyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class Product
{
    public int ProductId { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class Purchase
{
    public int PurchaseId { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// ★ Phase 7 — 감사로그 분리 실증.
///
/// 구매 이력처럼 법적 보존 의무가 있는 기록은 로그 파이프라인을 타지 않고 여기로 온다.
/// 이유는 딱 하나다: 로그 파이프라인은 유실을 허용하도록 설계돼 있다
/// (fluentd-async: true — 수집기가 죽어도 앱은 계속 돌고 로그는 버려진다).
/// 유실을 허용하는 경로에 "누가 언제 무엇을 조회/구매했는가"를 태울 수는 없다.
/// 게다가 ISM 정책으로 N일 뒤 자동 삭제되는 저장소는 보존 의무와 정면으로 충돌한다.
/// </summary>
public sealed class AuditLog
{
    public long AuditLogId { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? Detail { get; set; }
    public string? TraceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // 스키마는 db/init/01-seed.sql 이 만든다. EF 마이그레이션은 쓰지 않는다.
        // PoC에서 스키마 소유권을 두 곳에 두면 어느 쪽이 진실인지 헷갈린다.
        b.Entity<Company>(e =>
        {
            e.ToTable("Companies");
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(100);
        });

        b.Entity<Product>(e =>
        {
            e.ToTable("Products");
            e.HasKey(x => x.ProductId);
            e.Property(x => x.CompanyId).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Price).HasPrecision(18, 2);
        });

        b.Entity<Purchase>(e =>
        {
            e.ToTable("Purchases");
            e.HasKey(x => x.PurchaseId);
            e.Property(x => x.CompanyId).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(x => x.AuditLogId);
            e.Property(x => x.CompanyId).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.UserId).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Action).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.TargetType).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.TargetId).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.TraceId).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
