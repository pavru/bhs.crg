using BHS.CRG.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class RenditionLayoutProfileConfiguration : IEntityTypeConfiguration<RenditionLayoutProfile>
{
    public const string Table = "rendition_layout_profiles";

    /// <summary>Уникальность имени без учёта регистра — индекс по выражению, его создаёт миграция.</summary>
    public const string NameIndex = "IX_rendition_layout_profiles_name";

    public void Configure(EntityTypeBuilder<RenditionLayoutProfile> b)
    {
        b.ToTable(Table);
        b.HasKey(e => e.Id);

        b.Property(e => e.Name).HasMaxLength(RenditionLayoutProfile.NameMax).IsRequired();
        // Перечни — строками, как везде в проекте: имя переживает перестановку значений.
        b.Property(e => e.Paper).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Orientation).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Fit).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Ignore(e => e.Margins);

        // Системный профиль один: частичный уникальный индекс держит это и при двух процессах, которые
        // сидят одновременно. Идентификатор у него константа кода — индекс ловит то, чего константа не
        // поймает: строку, ставшую системной иным путём.
        b.HasIndex(e => e.IsSystem).IsUnique().HasFilter("\"IsSystem\"");

        // ⚠️ Индекса по имени здесь нет нарочно: он по выражению lower("Name") — «Узкий» и «узкий»
        // человек в меню не различит, — а такой индекс моделью не выразить. Его создаёт миграция
        // RenditionLayoutProfiles.
    }
}
