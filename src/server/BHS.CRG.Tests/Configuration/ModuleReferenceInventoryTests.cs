using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись колонок-ссылок модуля (ТЗ CORE-34.1, issue #1094): каждая колонка модели, где может лежать
/// идентификатор записи ядра, названа в <see cref="IAppModule.References" />.
///
/// <para>Удалению это НЕ нужно — держателей находит скан схемы, и забытая колонка удержит запись всё
/// равно. Нужно человеку: о забытой колонке отказ скажет «записей — 40» вместо «строки счетов с этой
/// позицией», и нужно обратному опросу (issue #1184) — без вида цели не понять, где искать, жива ли
/// она. Перепись ловит забытое здесь, а не на экране заказчика.</para>
/// </summary>
public class ModuleReferenceInventoryTests
{
    /// <summary>Модули сборки со своей схемой и модель каждого. Новый модуль вписывается сюда.</summary>
    private static IEnumerable<(IAppModule Module, IModel Model)> Modules()
    {
        // Подключения модель не требует: адрес не используется, пока к базе не обратились.
        var options = new DbContextOptionsBuilder<CostsDbContext>();
        CostsDbContext.Configure(options, "Host=127.0.0.1;Database=never");
        using var costs = new CostsDbContext(options.Options);
        yield return (new CostsModule(), costs.Model);
    }

    private static readonly string[] ReferenceTypes = ["uuid", "uuid[]", "json", "jsonb", "json[]", "jsonb[]"];

    private sealed record Column(string Table, string Name, string Type, bool Internal, IProperty Property);

    /// <summary>Колонки, где идентификатор ядра лежать МОЖЕТ: всё, кроме своих и внутренних ключей.</summary>
    private static List<Column> Candidates(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => new Column(
                e.GetTableName()!, p.GetColumnName(), p.GetColumnType(),
                Internal: p.IsPrimaryKey() || p.IsForeignKey(), p)))
            .Where(c => ReferenceTypes.Contains(c.Type) && !c.Internal)
            .ToList();

    [Fact]
    public void Каждая_колонка_с_идентификатором_объявлена()
    {
        var forgotten = new List<string>();
        foreach (var (module, model) in Modules())
        foreach (var column in Candidates(model))
            if (!module.References.Any(r => r.Table == column.Table && r.Column == column.Name))
                forgotten.Add($"{module.Code}: {column.Table}.{column.Name} ({column.Type})");

        Assert.True(forgotten.Count == 0,
            "Колонки модуля, где может лежать идентификатор записи ядра, не объявлены:\n" +
            string.Join("\n", forgotten) + "\n\n" +
            "Удаление записи ядра такая колонка остановит и без объявления — её найдёт скан схемы. Но " +
            "отказ назовёт её числом без слов, а поиск потерянных ссылок её не проверит. Объявите её в " +
            "References модуля: ModuleReference.Holding — что держит, или Remembering — почему не держит.");
    }

    /// <summary>Объявление о колонке, которой в модели нет, — след переименования: слова отвалятся молча.</summary>
    [Fact]
    public void Объявления_называют_существующие_колонки()
    {
        var dead = new List<string>();
        foreach (var (module, model) in Modules())
        {
            var columns = model.GetEntityTypes()
                .SelectMany(e => e.GetProperties().Select(p => (Table: e.GetTableName()!, Name: p.GetColumnName())))
                .ToHashSet();

            foreach (var r in module.References)
            {
                if (!columns.Contains((r.Table, r.Column)))
                    dead.Add($"{module.Code}: {r.Table}.{r.Column}");
                if (r.Document is { } doc && !new[] { (doc.Table, doc.LabelColumn), (doc.Table, doc.Key), (r.Table, doc.Via) }
                        .All(columns.Contains))
                    dead.Add($"{module.Code}: {r.Table}.{r.Column} — документ {doc.Table}.{doc.LabelColumn} через {doc.Via}");
            }
        }

        Assert.True(dead.Count == 0,
            "Объявления ссылок называют колонки, которых в модели модуля нет:\n" + string.Join("\n", dead));
    }

    /// <summary>
    /// Вопрос «кто держит» задаётся при удалении каждой записи справочника — по каждой держащей
    /// колонке. Без индекса это полный проход таблицы строк счетов на каждое удаление.
    ///
    /// <para>Тип и учётная запись — не в счёт: значений там единицы, индекс планировщику не помог бы, а
    /// удаляют их редко. JSON — тоже: искать в нём индексом нечем.</para>
    /// </summary>
    [Fact]
    public void Держащая_колонка_идентификатор_стоит_первой_в_индексе()
    {
        var bare = new List<string>();
        foreach (var (module, model) in Modules())
        foreach (var r in module.References.Where(r => r.Holds
                     && r.Target is not (null or ReferenceTarget.DocumentType or ReferenceTarget.User)))
        {
            var entity = model.GetEntityTypes().Single(e => e.GetTableName() == r.Table);
            var leads = entity.GetIndexes().Any(i => i.Properties[0].GetColumnName() == r.Column);
            if (!leads) bare.Add($"{module.Code}: {r.Table}.{r.Column}");
        }

        Assert.True(bare.Count == 0,
            "У держащих колонок нет индекса, где колонка стоит первой:\n" + string.Join("\n", bare));
    }

    [Fact]
    public void У_каждого_объявления_есть_слова()
    {
        foreach (var (module, _) in Modules())
        foreach (var r in module.References)
            Assert.False(string.IsNullOrWhiteSpace(r.What),
                $"{module.Code}: {r.Table}.{r.Column} — не сказано, что колонка держит или почему не держит.");
    }

    /// <summary>
    /// У держащей колонки-идентификатора назван вид цели (issue #1184). Без него обратный опрос не знает,
    /// в какой таблице ядра искать, и колонка уходит в «не проверено» — навсегда и молча для того, кто её
    /// объявил. Без вида живёт только JSON: цели в нём разные.
    /// </summary>
    [Fact]
    public void У_держащей_колонки_идентификатора_назван_вид_цели()
    {
        var blind = new List<string>();
        foreach (var (module, model) in Modules())
        foreach (var column in Candidates(model).Where(c => c.Type is "uuid" or "uuid[]"))
            if (module.References.FirstOrDefault(r => r.Table == column.Table && r.Column == column.Name) is { Holds: true, Target: null })
                blind.Add($"{module.Code}: {column.Table}.{column.Name}");

        Assert.True(blind.Count == 0,
            "У держащих колонок не назван вид цели (ReferenceTarget):\n" + string.Join("\n", blind) + "\n\n" +
            "Поиск потерянных ссылок такую колонку не проверит: ему негде искать запись.");
    }

    /// <summary>
    /// У каждого вида цели есть таблица ядра — и она есть в модели. Новое значение
    /// <see cref="ReferenceTarget" /> без строки соответствия роняло бы опрос на первой же колонке.
    /// </summary>
    [Fact]
    public void У_каждого_вида_цели_есть_таблица_ядра()
    {
        var options = new DbContextOptionsBuilder<BHS.CRG.Infrastructure.Persistence.AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=never").Options;
        using var core = new BHS.CRG.Infrastructure.Persistence.AppDbContext(options);
        var scan = new BHS.CRG.Infrastructure.Persistence.ModuleLostReferenceScan(core);
        var entities = BHS.CRG.Api.Modules.Ports.ModuleReferenceTargetsPort.Entities;

        foreach (var target in Enum.GetValues<ReferenceTarget>())
        {
            Assert.True(entities.ContainsKey(target), $"У вида цели {target} нет строки в ModuleReferenceTargetsPort.Entities.");
            var table = scan.TableOf(entities[target]);
            Assert.False(string.IsNullOrEmpty(table.Table));
            Assert.Equal("public", table.Schema);
        }
    }
}
