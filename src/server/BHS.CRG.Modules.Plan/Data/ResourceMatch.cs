using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>По чему запомненное узнаёт ресурс сметы.</summary>
public enum ResourceMatchKind
{
    /// <summary>По коду ресурса из файла. Старше наименования: код называет ресурс, а не описывает его.</summary>
    Code,

    /// <summary>По наименованию ресурса — когда кода в файле нет.</summary>
    Name,
}

/// <summary>
/// Запомненное сопоставление «код ресурса или его наименование → позиция номенклатуры» (change
/// <c>plan-08-matching</c>, ТЗ PLAN-9.1, CORE-9, D8 зонтика): выбор сметчика подставляется в материал
/// следующей редакции сам, с пометкой «запомнено». По образцу <c>SupplierMatch</c> модуля <c>costs</c>.
///
/// <para><b>Ключ без поставщика и без сметы</b>: код ресурса — слово сборника, а не отдельного расчёта.
/// Неверное запомненное перенацеливают в списке.</para>
///
/// <para>Коэффициента нет, как у <c>SupplierMatch</c>: запомненное отвечает на один вопрос — «какая это
/// позиция», а количество материала остаётся в единице файла.</para>
/// </summary>
public sealed class ResourceMatch
{
    /// <summary>Длина отпечатка ключа: SHA-256 шестнадцатеричной строкой.</summary>
    public const int KeyHashLength = 64;

    public const int ByNameLength = 256;

    private ResourceMatch() { }

    public Guid Id { get; private set; }

    public ResourceMatchKind Kind { get; private set; }

    /// <summary>Отпечаток приведённого кода или наименования — по нему запомненное единственно.</summary>
    public string KeyHash { get; private set; } = null!;

    /// <summary>Код или наименование, КАК В ФАЙЛЕ: его видит человек в списке, и по нему ключ пересчитывается,
    /// если правило приведения изменится.</summary>
    public string SourceText { get; private set; } = null!;

    /// <summary>Позиция номенклатуры ядра — без внешнего ключа (ТЗ CORE-34.1).</summary>
    public Guid NomenclatureId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Кто запомнил или перенацелил последним — справка, а не связь.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Имя запомнившего снимком: учётную запись могут удалить, а вопрос «кто это запомнил» останется.</summary>
    public string? UpdatedByName { get; private set; }

    public static ResourceMatch Create(ResourceMatchKey key, Guid nomenclatureId, Guid? by, string? byName)
    {
        var match = new ResourceMatch
        {
            Id = Guid.CreateVersion7(),
            Kind = key.Kind,
            KeyHash = key.Hash,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        match.Point(key.Source, nomenclatureId, by, byName);
        return match;
    }

    /// <summary>Направить запомненное на позицию. Текст ключа кладётся заново: написание в свежем файле могло
    /// отличаться, а список показывает то, что человек видел последним.</summary>
    public void Point(string source, Guid nomenclatureId, Guid? by, string? byName)
    {
        SourceText = source;
        NomenclatureId = nomenclatureId;
        UpdatedBy = by;
        UpdatedByName = PlanText.Clip(byName, ByNameLength);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static void Map(ModelBuilder builder)
    {
        var match = builder.Entity<ResourceMatch>();
        match.ToTable("resource_matches");
        match.HasKey(m => m.Id);
        match.Property(m => m.Id).HasColumnName("id");
        match.Property(m => m.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        match.Property(m => m.KeyHash).HasColumnName("key_hash").HasMaxLength(KeyHashLength).IsFixedLength();
        match.Property(m => m.SourceText).HasColumnName("source_text");
        match.Property(m => m.NomenclatureId).HasColumnName("nomenclature_id");
        match.Property(m => m.CreatedAt).HasColumnName("created_at");
        match.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        match.Property(m => m.UpdatedBy).HasColumnName("updated_by");
        match.Property(m => m.UpdatedByName).HasColumnName("updated_by_name").HasMaxLength(ByNameLength);

        // Версия — xmin: её называет правка из списка (If-Match), и она же ловит гонку запоминания.
        match.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();

        // Единственность — правило таблицы: два сопоставления одного ресурса разом иначе дали бы две записи,
        // и подставлялась бы «какая попадётся».
        match.HasIndex(m => new { m.Kind, m.KeyHash }).IsUnique().HasDatabaseName(PlanDbContext.ResourceMatchKey);

        // Удаление позиции номенклатуры спрашивает, кто её держит, — по этой колонке (ТЗ CORE-34.1).
        match.HasIndex(m => m.NomenclatureId).HasDatabaseName("ix_resource_matches_nomenclature_id");
    }
}

/// <summary>Ключ ресурса: по чему его узнавать и как это записано в файле.</summary>
/// <param name="Source">Текст как в файле, без краевых пробелов.</param>
/// <param name="Hash">Отпечаток приведённого текста.</param>
public sealed record ResourceMatchKey(ResourceMatchKind Kind, string Source, string Hash)
{
    /// <summary>
    /// Ключ, по которому ресурс ЗАПОМИНАЮТ: код, если он есть, иначе наименование; <c>null</c> — узнавать не по
    /// чему. Ровно один ключ, а не оба: у ресурса было бы два запомненных, и правка одного оставляла бы второе
    /// подставлять прежнее.
    /// </summary>
    public static ResourceMatchKey? Of(string? code, string? name) =>
        Of(ResourceMatchKind.Code, code) ?? Of(ResourceMatchKind.Name, name);

    /// <summary>
    /// Ключи, по которым ресурс ИЩУТ, — в порядке старшинства. Ресурс с кодом ищется и по наименованию:
    /// запомненное по ресурсу без кода (материал, вписанный сметчиком руками) иначе не нашло бы тот же
    /// материал с кодом.
    /// </summary>
    public static IEnumerable<ResourceMatchKey> Sought(string? code, string? name)
    {
        if (Of(ResourceMatchKind.Code, code) is { } byCode) yield return byCode;
        if (Of(ResourceMatchKind.Name, name) is { } byName) yield return byName;
    }

    public static ResourceMatchKey? Of(ResourceMatchKind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = Normalize(text);
        return normalized.Length == 0
            ? null
            : new(kind, text.Trim(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    /// <summary>
    /// Приведение к ключу: регистр, «ё» и пробелы. И только — то же правило, что у <c>SupplierMatchKey</c>:
    /// «3х2,5» и «3x2.5» здесь РАЗНЫЕ ключи нарочно. Ошибка «не узнал» стоит одного выбора, ошибка «узнал не
    /// то» — неверной позиции в каждой следующей редакции.
    /// </summary>
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        var gap = false;
        foreach (var symbol in text.Trim())
        {
            if (char.IsWhiteSpace(symbol))
            {
                gap = true;
                continue;
            }
            if (gap) result.Append(' ');
            gap = false;
            var lower = char.ToLowerInvariant(symbol);
            result.Append(lower == 'ё' ? 'е' : lower);
        }
        return result.ToString();
    }
}
