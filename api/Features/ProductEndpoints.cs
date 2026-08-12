using Microsoft.EntityFrameworkCore;
using PocApi.Data;
using PocApi.Logging;

namespace PocApi.Features;

public record ProductDto(int ProductId, string CompanyId, string Name, decimal Price, int Stock);

public record ProductRequest(string? Name, decimal Price, int Stock);

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/products");

        // ── 조회 (목록) ─────────────────────────────────────────────────────
        g.MapGet("/", async (HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();

            var items = await db.Products
                .Where(p => p.CompanyId == company && !p.IsDeleted)
                .OrderBy(p => p.ProductId)
                .Select(p => new ProductDto(p.ProductId, p.CompanyId, p.Name, p.Price, p.Stock))
                .ToListAsync();

            AppLog.Success(
                LogSchema.Events.ProductList,
                $"상품 목록을 조회했습니다 ({items.Count}건)");

            return Results.Ok(items);
        });

        // ── 조회 (단건) ─────────────────────────────────────────────────────
        g.MapGet("/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();

            var product = await db.Products
                .FirstOrDefaultAsync(p => p.ProductId == id && p.CompanyId == company && !p.IsDeleted);

            if (product is null)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductGet,
                    "존재하지 않는 상품을 조회했습니다",
                    new() { [LogSchema.ProductId] = id });

                return Results.NotFound(new { error = "product_not_found", productId = id });
            }

            AppLog.Success(
                LogSchema.Events.ProductGet,
                "상품을 조회했습니다",
                new() { [LogSchema.ProductId] = id });

            return Results.Ok(new ProductDto(
                product.ProductId, product.CompanyId, product.Name, product.Price, product.Stock));
        });

        // ── 등록 ────────────────────────────────────────────────────────────
        g.MapPost("/", async (ProductRequest req, HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();
            var errors = Validate(req);

            if (errors.Count > 0)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductCreate,
                    $"유효성 검증에 실패하여 상품을 등록하지 못했습니다: {string.Join(", ", errors)}");

                return Results.BadRequest(new { error = "validation_failed", details = errors });
            }

            var product = new Product
            {
                CompanyId = company,
                Name = req.Name!.Trim(),
                Price = req.Price,
                Stock = req.Stock,
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            AppLog.Success(
                LogSchema.Events.ProductCreate,
                "상품을 등록했습니다",
                new() { [LogSchema.ProductId] = product.ProductId });

            return Results.Created($"/api/products/{product.ProductId}",
                new ProductDto(product.ProductId, product.CompanyId, product.Name, product.Price, product.Stock));
        });

        // ── 수정 ────────────────────────────────────────────────────────────
        g.MapPut("/{id:int}", async (int id, ProductRequest req, HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();
            var errors = Validate(req);

            if (errors.Count > 0)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductUpdate,
                    $"유효성 검증에 실패하여 상품을 수정하지 못했습니다: {string.Join(", ", errors)}",
                    new() { [LogSchema.ProductId] = id });

                return Results.BadRequest(new { error = "validation_failed", details = errors });
            }

            var product = await db.Products
                .FirstOrDefaultAsync(p => p.ProductId == id && p.CompanyId == company && !p.IsDeleted);

            if (product is null)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductUpdate,
                    "존재하지 않는 상품을 수정하려 했습니다",
                    new() { [LogSchema.ProductId] = id });

                return Results.NotFound(new { error = "product_not_found", productId = id });
            }

            product.Name = req.Name!.Trim();
            product.Price = req.Price;
            product.Stock = req.Stock;
            await db.SaveChangesAsync();

            AppLog.Success(
                LogSchema.Events.ProductUpdate,
                "상품을 수정했습니다",
                new() { [LogSchema.ProductId] = id });

            return Results.Ok(new ProductDto(
                product.ProductId, product.CompanyId, product.Name, product.Price, product.Stock));
        });

        // ── 삭제 ────────────────────────────────────────────────────────────
        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, AppDbContext db) =>
        {
            var company = ctx.CompanyId();

            var product = await db.Products
                .FirstOrDefaultAsync(p => p.ProductId == id && p.CompanyId == company);

            if (product is null)
            {
                AppLog.Failure(
                    LogSchema.Events.ProductDelete,
                    "존재하지 않는 상품을 삭제하려 했습니다",
                    new() { [LogSchema.ProductId] = id });

                return Results.NotFound(new { error = "product_not_found", productId = id });
            }

            if (product.IsDeleted)
            {
                // 이미 삭제된 리소스를 다시 삭제 — 409. 데모의 "삭제 실패" 버튼이 노리는 경로다.
                AppLog.Failure(
                    LogSchema.Events.ProductDelete,
                    "이미 삭제된 상품을 다시 삭제하려 했습니다",
                    new() { [LogSchema.ProductId] = id });

                return Results.Conflict(new { error = "product_already_deleted", productId = id });
            }

            product.IsDeleted = true;
            await db.SaveChangesAsync();

            AppLog.Success(
                LogSchema.Events.ProductDelete,
                "상품을 삭제했습니다",
                new() { [LogSchema.ProductId] = id });

            return Results.NoContent();
        });
    }

    private static List<string> Validate(ProductRequest req)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            errors.Add("상품명은 비어 있을 수 없습니다");
        }

        if (req.Price <= 0)
        {
            errors.Add("가격은 0보다 커야 합니다");
        }

        if (req.Stock < 0)
        {
            errors.Add("재고는 음수일 수 없습니다");
        }

        return errors;
    }
}
