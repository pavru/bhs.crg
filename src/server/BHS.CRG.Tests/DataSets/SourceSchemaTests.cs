using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Описание колонок источника, которое получает клиент (issue #1133): колонка с объявленным видом едет
/// со СВОИМИ операторами отбора — из того же списка, по которому отбор проверяется. Диалог отбора
/// предлагает ровно их, и второго списка «вид → операторы» в клиенте нет.
/// </summary>
public class SourceSchemaTests
{
    private static readonly DataSetColumnInfo[] Columns =
    [
        new("Номер", ["СЧ-1"]), new("Итого", []), new("Срок", ["2026-05-01"]), new("Расчёт", ["7"]),
    ];

    private static JsonElement Column(JsonElement schema, string name) =>
        schema.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void Колонка_с_видом_едет_со_своими_операторами_а_закрытая_с_причиной()
    {
        var types = new DataSetColumnTypes(
            new Dictionary<string, string> { ["Номер"] = TableOperators.Text, ["Срок"] = TableOperators.Date },
            new Dictionary<string, string> { ["Итого"] = "нет права на суммы" });

        var schema = JsonDocument.Parse(DataSetDtoMapper.SerializeSchema(Columns, types)).RootElement;

        var due = Column(schema, "Срок");
        Assert.Equal("date", due.GetProperty("kind").GetString());
        Assert.Equal(TableOperators.For(TableOperators.Date), due.GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
        Assert.DoesNotContain("contains", due.GetProperty("operators").EnumerateArray().Select(o => o.GetString()));

        Assert.Contains("contains", Column(schema, "Номер").GetProperty("operators").EnumerateArray().Select(o => o.GetString()));

        // Закрытая колонка — с причиной и без операторов: условие по ней исполнитель отвергнет.
        var total = Column(schema, "Итого");
        Assert.Equal("нет права на суммы", total.GetProperty("unavailable").GetString());
        Assert.False(total.TryGetProperty("operators", out _));

        // Колонка, о которой поставщик ничего не объявил (вычисляемая), — без вида: сравнение по догадке.
        Assert.False(Column(schema, "Расчёт").TryGetProperty("kind", out _));
    }

    /// <summary>
    /// Колонка-выбор едет с перечнем своих значений (G1d, issue #1091): диалог отбора предлагает их
    /// списком. У остальных колонок ключа перечня нет вовсе.
    /// </summary>
    [Fact]
    public void Колонка_выбор_едет_с_перечнем_значений()
    {
        var types = new DataSetColumnTypes(
            new Dictionary<string, string> { ["Номер"] = TableOperators.Choice, ["Срок"] = TableOperators.Date },
            new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<string>> { ["Номер"] = ["Первый", "Второй"] });

        var schema = JsonDocument.Parse(DataSetDtoMapper.SerializeSchema(Columns, types)).RootElement;

        var choice = Column(schema, "Номер");
        Assert.Equal("choice", choice.GetProperty("kind").GetString());
        Assert.Equal(["Первый", "Второй"], choice.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(["eq", "neq", "in", "not_in"], choice.GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
        Assert.False(Column(schema, "Срок").TryGetProperty("options", out _));
    }

    /// <summary>
    /// У набора без видов запись прежняя, ключ в ключ: она лежит в базе у каждого файлового источника,
    /// и новые ключи с null изменили бы её форму без единой причины.
    /// </summary>
    [Fact]
    public void У_набора_без_видов_запись_колонок_прежняя()
    {
        Assert.Equal(
            """[{"name":"Number","sampleValues":["A-1"]}]""",
            DataSetDtoMapper.SerializeSchema([new DataSetColumnInfo("Number", ["A-1"])]));
    }
}
