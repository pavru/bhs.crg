using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Что запомнено за шифром: вид работы, единица классификатора, коэффициент и единица расценки.</summary>
/// <param name="Factor">Множитель «объём расценки → объём в единице классификатора», больше нуля.</param>
/// <param name="SourceUnit">Единица расценки, от которой считался коэффициент, — как в файле.</param>
public sealed record RateMatchValue(Guid WorkTypeId, Guid UnitId, decimal Factor, string? SourceUnit);

/// <summary>
/// Запомненное сопоставление «шифр расценки (+ нормативная база) → вид работы, единица классификатора,
/// коэффициент пересчёта» (change <c>plan-08-matching</c>, ТЗ PLAN-6.1, D8 зонтика <c>plan-00-epic</c>) — по
/// образцу <c>SupplierMatch</c> модуля <c>costs</c>. Смета на 300 строк руками каждый раз не сопоставится:
/// выбор сметчика запоминается и при следующем импорте подставляется сам, с пометкой «запомнено».
///
/// <para><b>Своей таблицей в схеме модуля</b>: на неё опирается код (единственность ключа, подстановка), а не
/// схема, которую правит администратор.</para>
///
/// <para><b>Ключ без поставщика и без сметы</b>: шифр — общий язык сборника, а не отдельного расчёта. Один
/// шифр в разных сметах может значить разные работы — поэтому запомненное только предлагается, с пометкой и
/// отменой, а список запомненного умеет «перенацелить».</para>
///
/// <para>⚠️ <b>Коэффициент обязателен</b> — в отличие от <c>SupplierMatch</c>, где его нет нарочно: расценка
/// ГЭСН считает в «100 м», классификатор и монтажник — в «м», и позиция, заведённая без пересчёта, никогда
/// не встретилась бы с фактом. Поэтому запомнена и единица расценки: файл, где у того же шифра единица другая,
/// запомненного не получает (<c>unit-changed</c>, D17 зонтика).</para>
/// </summary>
public sealed class RateMatch
{
    /// <summary>Длина отпечатка ключа: SHA-256 шестнадцатеричной строкой.</summary>
    public const int KeyHashLength = 64;

    public const int ByNameLength = 256;

    private RateMatch() { }

    public Guid Id { get; private set; }

    /// <summary>Отпечаток приведённого шифра и базы — по нему сопоставление единственно (как у <c>SupplierMatch</c>).</summary>
    public string KeyHash { get; private set; } = null!;

    /// <summary>Шифр, КАК ОН СТОЯЛ В ФАЙЛЕ: его видит человек в списке, и по нему ключ пересчитывается, если
    /// правило приведения изменится.</summary>
    public string RateCode { get; private set; } = null!;

    /// <summary>Нормативная база ключа — уже приведённая правилом <see cref="RateMatchKey" />; <c>null</c> — файл
    /// её не назвал.</summary>
    public string? NormativeBase { get; private set; }

    /// <summary>Вид работы классификатора — запись ядра, без внешнего ключа (ТЗ CORE-34.1).</summary>
    public Guid WorkTypeId { get; private set; }

    /// <summary>Единица классификатора — запись ядра.</summary>
    public Guid UnitId { get; private set; }

    public decimal Factor { get; private set; }

    /// <summary>Единица расценки, от которой считался <see cref="Factor" />, — как в файле.</summary>
    public string? SourceUnit { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Кто запомнил или перенацелил последним — справка, а не связь.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>Имя запомнившего снимком: учётную запись могут удалить, а вопрос «кто это запомнил» останется.</summary>
    public string? UpdatedByName { get; private set; }

    public RateMatchValue Value => new(WorkTypeId, UnitId, Factor, SourceUnit);

    public static RateMatch Create(RateMatchKey key, RateMatchValue value, Guid? by, string? byName)
    {
        var match = new RateMatch
        {
            Id = Guid.CreateVersion7(),
            KeyHash = key.Hash,
            NormativeBase = key.NormativeBase,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        match.Point(key.RateCode, value, by, byName);
        return match;
    }

    /// <summary>
    /// Направить запомненное на вид работы, единицу и коэффициент. Шифр кладётся заново: приведённый ключ тот
    /// же, а написание в свежем файле могло отличаться — список показывает то, что человек видел последним.
    /// </summary>
    public void Point(string rateCode, RateMatchValue value, Guid? by, string? byName)
    {
        RateCode = rateCode;
        WorkTypeId = value.WorkTypeId;
        UnitId = value.UnitId;
        Factor = value.Factor;
        SourceUnit = PlanText.Clip(value.SourceUnit, EstimateRow.UnitLength);
        UpdatedBy = by;
        UpdatedByName = PlanText.Clip(byName, ByNameLength);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static void Map(ModelBuilder builder)
    {
        var match = builder.Entity<RateMatch>();
        match.ToTable("rate_matches");
        match.HasKey(m => m.Id);
        match.Property(m => m.Id).HasColumnName("id");
        match.Property(m => m.KeyHash).HasColumnName("key_hash").HasMaxLength(KeyHashLength).IsFixedLength();
        match.Property(m => m.RateCode).HasColumnName("rate_code");
        match.Property(m => m.NormativeBase).HasColumnName("normative_base");
        match.Property(m => m.WorkTypeId).HasColumnName("work_type_id");
        match.Property(m => m.UnitId).HasColumnName("unit_id");
        match.Property(m => m.Factor).HasColumnName("factor").HasPrecision(18, 6);
        match.Property(m => m.SourceUnit).HasColumnName("source_unit").HasMaxLength(EstimateRow.UnitLength);
        match.Property(m => m.CreatedAt).HasColumnName("created_at");
        match.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        match.Property(m => m.UpdatedBy).HasColumnName("updated_by");
        match.Property(m => m.UpdatedByName).HasColumnName("updated_by_name").HasMaxLength(ByNameLength);
        match.Ignore(m => m.Value);

        // Версия — xmin: её называет правка из списка (If-Match), и она же ловит гонку запоминания.
        match.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();

        // Единственность — правило таблицы, а не обещание кода: два сопоставления одного шифра разом иначе
        // дали бы две записи, и подставлялась бы «какая попадётся».
        match.HasIndex(m => m.KeyHash).IsUnique().HasDatabaseName(PlanDbContext.RateMatchKey);

        // Удаление вида работы и единицы спрашивает, кто их держит, — по этим колонкам (ТЗ CORE-34.1).
        match.HasIndex(m => m.WorkTypeId).HasDatabaseName("ix_rate_matches_work_type_id");
        match.HasIndex(m => m.UnitId).HasDatabaseName("ix_rate_matches_unit_id");
    }
}

/// <summary>
/// Ключ запомненного шифра: приведённый шифр расценки плюс нормативная база, если файл её называет (design
/// <c>plan-08</c>). «ГЭСНм08-02-399-01» в ФЕР-2020 и в ФСНБ-2022 — одна работа, но единицы сборников могут
/// различаться; от этого и база в ключе.
/// </summary>
/// <param name="RateCode">Шифр как в файле, без краевых пробелов.</param>
/// <param name="NormativeBase">База ключа, приведённая <see cref="BaseOf" />; <c>null</c> — файл её не назвал.</param>
/// <param name="Hash">Отпечаток приведённых шифра и базы.</param>
public sealed record RateMatchKey(string RateCode, string? NormativeBase, string Hash)
{
    /// <summary>Ключ строки; <c>null</c> — шифра нет, и узнавать строку не по чему.</summary>
    /// <param name="regionName">База, как её назвал файл (<see cref="EstimateRevision.NormativeBase" />).</param>
    public static RateMatchKey? Of(string? rateCode, string? regionName)
    {
        if (string.IsNullOrWhiteSpace(rateCode)) return null;
        var code = Squeeze(rateCode).ToUpperInvariant();
        if (code.Length == 0) return null;

        var normativeBase = BaseOf(regionName);
        var text = $"{code}\u001f{normativeBase?.ToUpperInvariant() ?? string.Empty}";
        return new(rateCode.Trim(), normativeBase,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>
    /// База ключа из названия базы в файле: часть до первой скобки, с одиночными пробелами. «ФСНБ-2022 (с Изм.
    /// 1-18.1)» → «ФСНБ-2022»: номер изменений в ключ не входит — иначе каждое обновление базы у сметчика
    /// стирало бы всю память сопоставлений.
    /// </summary>
    public static string? BaseOf(string? regionName)
    {
        if (string.IsNullOrWhiteSpace(regionName)) return null;
        var bracket = regionName.IndexOf('(');
        var head = (bracket >= 0 ? regionName[..bracket] : regionName).Trim();
        var result = string.Join(' ', head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }

    /// <summary>Без пробелов вовсе, неразрывных тоже: «ГЭСНм 08-02-399-01» и «ГЭСНм08-02-399-01» — один шифр.</summary>
    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
