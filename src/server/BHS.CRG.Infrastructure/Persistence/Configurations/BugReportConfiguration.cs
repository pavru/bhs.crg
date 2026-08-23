using BHS.CRG.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class BugReportConfiguration : IEntityTypeConfiguration<BugReport>
{
    public void Configure(EntityTypeBuilder<BugReport> b)
    {
        b.ToTable("bug_reports");
        b.HasKey(e => e.Id);
        b.Property(e => e.AuthorId).IsRequired();
        b.Property(e => e.Message).IsRequired();
        // Строкой, а не jsonb: техблок сервер не ищет и не фильтрует — он его только отдаёт клиенту
        // и подставляет в заготовку. jsonb дал бы разбор при каждой записи и ничего взамен.
        b.Property(e => e.TechContext);
        b.Property(e => e.ScreenshotBlobPath);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.IssueDraft);
        b.Property(e => e.GithubIssueNumber);
        b.Property(e => e.GithubIssueUrl);
        b.Property(e => e.FixedInVersion).HasMaxLength(32);
        // Список открывается по «новые сверху», разбор идёт по статусу — эти два и индексируем.
        b.HasIndex(e => e.CreatedAt);
        b.HasIndex(e => e.Status);
        b.HasIndex(e => e.AuthorId);
    }
}
