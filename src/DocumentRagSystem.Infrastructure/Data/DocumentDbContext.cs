using Microsoft.EntityFrameworkCore;

namespace DocumentRagSystem.Infrastructure.Data;

public class DocumentDbContext : DbContext
{
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DocumentDbContext(DbContextOptions<DocumentDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DocumentEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.FileName);
            entity.HasIndex(e => e.ArticleId);
        });
    }
}
