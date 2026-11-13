using System.Text.RegularExpressions;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись мест модулей, показывающих организацию (ТЗ COST-6, issue #1473): название организации модуль
/// берёт ТОЛЬКО у порта справочника, а не хранит у себя.
///
/// <para>Правило «сокращённое, если оно есть» живёт в порте ядра, и пятнадцать мест <c>costs</c> получают
/// его сами — потому что других источников названия у них нет: ссылки на ядро у модуля нет вовсе
/// (<c>ModuleBoundaryTests</c>). Единственная дверь мимо порта — СВОЯ колонка с названием, записанным когда-то
/// (снимок, журнал): она показала бы полное название навсегда, и правило её не достало бы. Перепись
/// ловит такую колонку здесь, а не на экране заказчика.</para>
///
/// <para>Смотрится ИМЯ колонки: имя таблицы («supplier_terms») про организацию говорит, а колонка
/// «updated_by_name» в ней — имя человека.</para>
/// </summary>
public class OrganizationShortNameCensusTests
{
    private static readonly Regex OrganizationName = new(
        "(supplier|payer|organization|party|vendor|contractor).*name|name.*(supplier|payer|organization|party)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Колонки с названием, которое НЕ запись справочника, — с причиной. Добавляя сюда, объясните, почему
    /// показ этой колонки не обязан следовать сокращённому названию.
    /// </summary>
    private static readonly Dictionary<string, string> NotDirectory = new(StringComparer.Ordinal)
    {
        // Поставщик коммерческого предложения из файла конъюнктурного анализа: текст файла, как он
        // прочитан, — ссылки на справочник у предложения нет (модуль plan).
        ["conjuncture_offers.supplier_name"] = "название поставщика из файла КА, не запись справочника",
    };

    private static IEnumerable<IModel> Models()
    {
        // Подключения модель не требует: адрес не используется, пока к базе не обратились.
        var costs = new DbContextOptionsBuilder<CostsDbContext>();
        CostsDbContext.Configure(costs, "Host=127.0.0.1;Database=never");
        using (var db = new CostsDbContext(costs.Options)) yield return db.Model;

        var plan = new DbContextOptionsBuilder<PlanDbContext>();
        PlanDbContext.Configure(plan, "Host=127.0.0.1;Database=never");
        using (var db = new PlanDbContext(plan.Options)) yield return db.Model;

        var work = new DbContextOptionsBuilder<WorkDbContext>();
        WorkDbContext.Configure(work, "Host=127.0.0.1;Database=never");
        using (var db = new WorkDbContext(work.Options)) yield return db.Model;
    }

    [Fact]
    public void Модули_не_хранят_названий_организаций()
    {
        var stored = Models()
            .SelectMany(m => m.GetEntityTypes())
            .SelectMany(e => e.GetProperties().Select(p => (Column: p.GetColumnName(), Full: $"{e.GetTableName()}.{p.GetColumnName()}")))
            .Where(c => OrganizationName.IsMatch(c.Column) && !NotDirectory.ContainsKey(c.Full))
            .Select(c => c.Full)
            .ToList();

        Assert.True(stored.Count == 0,
            "Колонка модуля хранит название организации — показ мимо порта справочника, и сокращённое " +
            $"название (issue #1473) её не достанет: {string.Join(", ", stored)}. Храните ссылку, называет порт.");
    }

    /// <summary>Сторож срабатывает: колонка, какую он ищет, им находится.</summary>
    [Theory]
    [InlineData("supplier_name")]
    [InlineData("PayerName")]
    [InlineData("organization_display_name")]
    public void Сторож_узнаёт_колонку_с_названием(string column) => Assert.Matches(OrganizationName, column);
}
