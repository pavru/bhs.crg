using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>По чему соответствие узнаёт строку поставщика.</summary>
public enum SupplierMatchKind
{
    /// <summary>По артикулу поставщика. Старше наименования: артикул называет товар, а не описывает его.</summary>
    Code,

    /// <summary>По наименованию из бумаги — когда артикула в строке нет.</summary>
    Name,
}

/// <summary>
/// Соответствие «поставщик + его артикул или наименование → позиция номенклатуры» (задача C3 этапа 2,
/// issue #1079, ТЗ COST-7.1). Один и тот же кабель у трёх поставщиков называется по-разному, и заводить
/// позицию на каждое название нельзя — поэтому выбор человека запоминается и подставляется в следующий
/// счёт того же поставщика.
///
/// <para><b>Своей таблицей в схеме модуля, а не типом в общей таблице.</b> Запись общей таблицы видна
/// набором «Общие данные» всякому с <c>core.catalog.read</c> — провайдер объявляет модуль <c>core</c> и
/// отбора по владельцу типа не делает. Здесь видимость решена местом хранения, а не отбором, который
/// однажды забыли бы: та же логика, что у инварианта H1 для сумм. И вторая причина — на таблицу
/// опирается КОД (единственность ключа, «заменить запомненное»), а не схема, которую правит
/// администратор.</para>
///
/// <para>⚠️ <b>Ключ — запись поставщика, а не ИНН.</b> Одна организация, заведённая в справочнике
/// дважды (роль, уровень), соответствий друг друга не видит. Свести их может только слияние дублей в
/// справочнике; ключ по ИНН потребовал бы читать данные записи ядра на каждой подстановке.</para>
///
/// <para>⚠️ <b>Коэффициента пересчёта единиц здесь нет и не будет:</b> поставщик продаёт барабанами,
/// учёт в метрах, и это отдельная задача со своим правилом. Соответствие отвечает на один вопрос —
/// «какая это позиция».</para>
/// </summary>
public sealed class SupplierMatch
{
    /// <summary>Длина отпечатка ключа: SHA-256 шестнадцатеричной строкой.</summary>
    public const int KeyHashLength = 64;

    /// <summary>Предел имени запомнившего — как у отображаемого имени учётной записи.</summary>
    public const int ByNameLength = 256;

    /// <summary>Для EF.</summary>
    private SupplierMatch() { }

    public Guid Id { get; private set; }

    public Guid SupplierId { get; private set; }

    public SupplierMatchKind Kind { get; private set; }

    /// <summary>
    /// Отпечаток приведённого ключа — по нему соответствие единственно у поставщика.
    ///
    /// <para>Отпечаток, а не сам текст: наименование в бумаге длиной не ограничено, а единственный
    /// индекс по длинному тексту база построить отказывается — и отказала бы на первой же строке с
    /// абзацем вместо названия.</para>
    /// </summary>
    public string KeyHash { get; private set; } = null!;

    /// <summary>
    /// Текст ключа, КАК ОН СТОЯЛ В БУМАГЕ. Его видит человек в списке соответствий, и по нему же ключи
    /// пересчитываются, если правило приведения изменится: отпечаток обратно в текст не превратить.
    /// </summary>
    public string SourceText { get; private set; } = null!;

    /// <summary>Позиция номенклатуры ядра. Без внешнего ключа — как у строки счёта.</summary>
    public Guid NomenclatureId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Кто запомнил или заменил последним. Помнит, а не держит: справка для разбора.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>
    /// Имя запомнившего — снимком на момент записи. Порта «имя по идентификатору» у модуля нет, и
    /// заводить его ради подписи незачем: учётную запись могут удалить, а вопрос «кто это запомнил»
    /// останется.
    /// </summary>
    public string? UpdatedByName { get; private set; }

    public static SupplierMatch Create(Guid supplierId, SupplierMatchKey key, Guid nomenclatureId, Guid? by, string? byName)
    {
        var match = new SupplierMatch
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplierId,
            Kind = key.Kind,
            KeyHash = key.Hash,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        match.Point(key.Source, nomenclatureId, by, byName);
        return match;
    }

    /// <summary>
    /// Направить соответствие на позицию. Исходный текст кладётся заново: приведённый ключ тот же, а
    /// написание в свежей бумаге могло отличаться — список обязан показывать то, что человек видел
    /// последним.
    /// </summary>
    public void Point(string source, Guid nomenclatureId, Guid? by, string? byName)
    {
        SourceText = source;
        NomenclatureId = nomenclatureId;
        UpdatedBy = by;
        UpdatedByName = byName is { Length: > ByNameLength } ? byName[..ByNameLength] : byName;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static void Map(ModelBuilder builder)
    {
        var match = builder.Entity<SupplierMatch>();
        match.ToTable("supplier_matches");
        match.HasKey(m => m.Id);

        match.Property(m => m.Id).HasColumnName("id");
        match.Property(m => m.SupplierId).HasColumnName("supplier_id");
        match.Property(m => m.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        match.Property(m => m.KeyHash).HasColumnName("key_hash").HasMaxLength(KeyHashLength).IsFixedLength();
        match.Property(m => m.SourceText).HasColumnName("source_text");
        match.Property(m => m.NomenclatureId).HasColumnName("nomenclature_id");
        match.Property(m => m.CreatedAt).HasColumnName("created_at");
        match.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        match.Property(m => m.UpdatedBy).HasColumnName("updated_by");
        match.Property(m => m.UpdatedByName).HasColumnName("updated_by_name").HasMaxLength(ByNameLength);

        // Единственность — правило таблицы, а не обещание кода: два одновременных сохранения разных
        // счётов одного поставщика иначе дали бы две записи на одну строку бумаги, и подставлялась бы
        // «какая попадётся».
        match.HasIndex(m => new { m.SupplierId, m.Kind, m.KeyHash })
            .IsUnique()
            .HasDatabaseName("ux_supplier_matches_key");

        // Удаление позиции спрашивает, кто её держит, — по этой колонке (ТЗ CORE-34.1).
        match.HasIndex(m => m.NomenclatureId).HasDatabaseName("ix_supplier_matches_nomenclature");
    }
}

/// <summary>
/// Ключ строки поставщика: по чему её узнавать и как это записано в бумаге.
/// </summary>
/// <param name="Source">Текст как в бумаге, без краевых пробелов.</param>
/// <param name="Hash">Отпечаток приведённого текста.</param>
public sealed record SupplierMatchKey(SupplierMatchKind Kind, string Source, string Hash)
{
    /// <summary>
    /// Ключ строки: артикул, если он есть, иначе наименование; <c>null</c> — узнавать строку не по чему.
    ///
    /// <para><b>Ровно один ключ на строку, а не оба.</b> Запомни мы и артикул, и наименование, у одной
    /// строки было бы два соответствия, и правка одного оставляла бы второе подставлять прежнее —
    /// «исправил, а оно опять».</para>
    /// </summary>
    public static SupplierMatchKey? Of(string? supplierCode, string? supplierText) =>
        Of(SupplierMatchKind.Code, supplierCode) ?? Of(SupplierMatchKind.Name, supplierText);

    /// <summary>
    /// Ключи, по которым строку ИЩУТ, — в порядке старшинства. Строка с артикулом ищется и по
    /// наименованию: распознавание артикул не читает, и соответствие, запомненное со скана, иначе не
    /// нашло бы ту же строку, набранную руками с артикулом.
    /// </summary>
    public static IEnumerable<SupplierMatchKey> Sought(string? supplierCode, string? supplierText)
    {
        if (Of(SupplierMatchKind.Code, supplierCode) is { } code) yield return code;
        if (Of(SupplierMatchKind.Name, supplierText) is { } name) yield return name;
    }

    public static SupplierMatchKey? Of(SupplierMatchKind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = Normalize(text);
        return normalized.Length == 0
            ? null
            : new(kind, text.Trim(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    /// <summary>
    /// Приведение текста к ключу: регистр, «ё» и пробелы. И только.
    ///
    /// <para>⚠️ <b>Осторожно нарочно.</b> «3х2,5» и «3x2.5» (кириллическая и латинская буква, запятая
    /// и точка) здесь РАЗНЫЕ ключи. Свести их хочется, но такое правило сводит и то, что сводить
    /// нельзя: «1,5» и «15» различаются одним знаком. Ошибка в сторону «не узнал» стоит человеку одного
    /// выбора; ошибка в сторону «узнал не то» — неверной позиции в каждом следующем счёте. Ослаблять
    /// правило — отдельным решением и с пересчётом ключей по <see cref="SupplierMatch.SourceText" />.</para>
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
