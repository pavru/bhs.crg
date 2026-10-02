using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Версия обработки источника (issue #1141) — отпечаток извлечения и обработки. От неё требуется
/// два противоположных свойства: меняться при любой правке того, что человек видел в диалоге, и НЕ
/// меняться от того, как значение записано, — иначе отказ получал бы тот, чью правку никто не трогал.
/// </summary>
public class SourceProcessingVersionTests
{
    private const string Filter = """{"type":"condition","column":"Итого","op":"between","values":["80","110"]}""";
    private const string Columns = """[{"alias":"К","expr":"1"}]""";
    private const string Sort = """[{"column":"Номер","direction":"asc"},{"column":"Итого","direction":"desc"}]""";

    private static string Of(
        string sheet = "Лист1", string? expressions = null, string? filter = Filter, string? columns = Columns,
        string? sort = Sort) => SourceProcessingVersion.Of(sheet, expressions, filter, columns, sort);

    /// <summary>
    /// База хранит части в <c>jsonb</c> и возвращает их другим текстом: ключи переставлены, пробелы
    /// свои, «1e2» стало «100». Отпечаток по тексту у только что сохранённого источника и у
    /// прочитанного из базы вышел бы разным — и человек получил бы отказ на собственную правку.
    /// </summary>
    [Theory]
    [InlineData("""{"a":1,"b":[1,2]}""", """{ "b" : [ 1, 2 ],  "a" : 1 }""")]
    [InlineData("""{"n":1e2}""", """{"n":100}""")]
    [InlineData("""{"n":1.0}""", """{"n":1}""")]
    [InlineData("""{"n":0.000001}""", """{"n":1e-6}""")]
    // В decimal не влезает: база вернёт такое число развёрнутым, и отпечаток обязан совпасть.
    [InlineData("""{"n":1e30}""", """{"n":1000000000000000000000000000000}""")]
    [InlineData("""{"n":-12.50E+1}""", """{"n":-125}""")]
    [InlineData("""{"n":-0.0}""", """{"n":0}""")]
    [InlineData("""{"s":"\u0410"}""", """{"s":"А"}""")]
    [InlineData("""{"a":1,"a":2}""", """{"a":2}""")]
    public void Запись_значения_на_версию_не_влияет(string asSent, string asStored)
    {
        Assert.Equal(Of(filter: asSent), Of(filter: asStored));
    }

    [Fact]
    public void Правка_любой_части_и_извлечения_меняет_версию()
    {
        var versions = new[]
        {
            Of(),
            Of(sheet: "Лист2"),
            Of(expressions: """[{"name":"Артикул","expr":"@id"}]"""),
            Of(filter: Filter.Replace("110", "120")),
            Of(filter: null),
            Of(columns: Columns.Replace("\"1\"", "\"2\"")),
            Of(columns: null),
            Of(sort: null),
            // Порядок уровней сортировки — часть её смысла, в отличие от порядка ключей объекта.
            Of(sort: """[{"column":"Итого","direction":"desc"},{"column":"Номер","direction":"asc"}]"""),
            // Число и строка с теми же цифрами — разные значения; нули в конце числа — значащие.
            Of(filter: """{"n":1}"""),
            Of(filter: """{"n":10}"""),
            Of(filter: """{"n":-1}"""),
            Of(filter: """{"n":0.1}"""),
            Of(filter: """{"n":"1"}"""),
        };

        Assert.Equal(versions.Length, versions.Distinct().Count());
    }

    /// <summary>Значение, оказавшееся в соседней части, — другая обработка, а не та же.</summary>
    [Fact]
    public void Значение_в_другой_части_даёт_другую_версию()
    {
        const string value = """[{"column":"А"}]""";

        Assert.NotEqual(Of(filter: null, columns: value, sort: null), Of(filter: null, columns: null, sort: value));
        Assert.NotEqual(Of(expressions: value, filter: null), Of(expressions: null, filter: value));
    }
}
