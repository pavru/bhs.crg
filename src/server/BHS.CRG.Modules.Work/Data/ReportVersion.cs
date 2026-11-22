using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>Содержимое отчёта, заменённое исправлением закрытого периода.</summary>
public sealed class ReportVersion : ModuleRecordVersion, IIsolatedRecord
{
    private ReportVersion() { }

    internal ReportVersion(Report report, Guid closingId, string content, string reason, ReportJournalActor actor, DateTimeOffset at)
        : base(report.Id, report.CurrentVersion, content, reason, actor.UserId, at)
    {
        ClosingId = closingId;
        AuthorName = actor.Name;
    }

    public Guid ClosingId { get; private set; }

    public string AuthorName { get; private set; } = "";

    internal static void Configure(ModelBuilder builder)
    {
        var version = ModuleRecordVersion.Map<ReportVersion, Report>(builder, "report_versions");
        version.Property(v => v.ClosingId).HasColumnName("closing_id");
        version.Property(v => v.AuthorName).HasColumnName("author_name").HasMaxLength(256);
    }
}
