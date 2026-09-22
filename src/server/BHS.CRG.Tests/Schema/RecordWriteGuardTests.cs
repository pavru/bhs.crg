using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Охрана записи (issue #957, ТЗ CORE-20): неверный вид значения и правка запертого поля обязаны
/// ОТКАЗАТЬ. Сторож здесь ломает запись — «аудит не находит нового» сторожем не является: это
/// чистота данных, а не работа охраны.
///
/// Каждое правило проверяется парой: запись, которая обязана отказать, и соседняя, которая обязана
/// пройти. Без второй половины тест остался бы зелёным, запрети мы сохранение вообще.
/// </summary>
public class RecordWriteGuardTests
{
    private static readonly Guid RecId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid RowId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid HeadId = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid IntId = Guid.Parse("a0000000-0000-0000-0000-0000000000c1");

    private const string Fields =
        """
        [{"key":"Кол","type":"number","title":"Количество"},
         {"key":"Табельный","type":"string","title":"Табельный номер","origin":"module"},
         {"key":"Сумма","type":"number","title":"Сумма","origin":"module","locked":true},
         {"key":"Шапка","type":"complex","typeId":"a0000000-0000-0000-0000-000000000003"},
         {"key":"Работы","type":"array","typeId":"a0000000-0000-0000-0000-000000000002"}]
        """;

    private static IReadOnlyDictionary<Guid, DocumentType> Types(
        SchemaEditLevel level = SchemaEditLevel.Extendable, Guid? parent = null) => new[]
    {
        T(RecId, "REC", parent, Fields, parent is null ? level : SchemaEditLevel.Open),
        T(RowId, "ROW", null, """[{"key":"Порядок","type":"number","title":"Порядок"}]"""),
        T(HeadId, "HEAD", null,
            """[{"key":"Итог","type":"number","title":"Итог","locked":true},{"key":"Примечание","type":"string"}]"""),
        // Родитель-модуль для проверки «замок не снимается наследованием».
        T(Guid.Parse("a0000000-0000-0000-0000-00000000000f"), "BASE", null, Fields, level),
    }.ToDictionary(t => t.Id);

    private static DocumentType T(Guid id, string code, Guid? parent, string fieldsJson,
        SchemaEditLevel level = SchemaEditLevel.Open) =>
        DocumentType.Restore(id, code, code, DocumentTypeKind.Composite, parent,
            JsonDocument.Parse($"{{\"fields\":{fieldsJson}}}"), JsonDocument.Parse("{}"), false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, editLevel: level);

    private static IReadOnlyDictionary<Guid, PrimitiveType> Prims() => new Dictionary<Guid, PrimitiveType>
    {
        [IntId] = PrimitiveType.Restore(IntId, "Цело число", "int", "number", null,
            JsonDocument.Parse("{\"integer\":true}"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<AuditIssue> Guard(string stored, string incoming,
        SchemaEditLevel level = SchemaEditLevel.Extendable, Guid? typeId = null, Guid? parent = null)
        => RecordWriteGuard.Refusals(J(stored), J(incoming), typeId ?? RecId, Types(level, parent), Prims());

    // ── Вид значения ──────────────────────────────────────────────────────────

    [Fact]
    public void Новое_значение_неверного_вида_отказывает_с_указанием_поля()
    {
        var refusals = Guard("""{"Кол":5}""", """{"Кол":"12,5"}""");

        var refusal = Assert.Single(refusals);
        Assert.Equal(SchemaDataAuditor.ValueType, refusal.Code);
        Assert.Equal("Кол", refusal.Path);
        Assert.Contains("Количество", refusal.Message);
    }

    [Fact]
    public void Значение_верного_вида_проходит()
    {
        Assert.Empty(Guard("""{"Кол":5}""", """{"Кол":7}"""));
    }

    /// <summary>
    /// ⚠️ Главное свойство охраны: она отказывает тому, что запись ВНОСИТ. Иначе четыре живых
    /// документа качества, где число листов лежит строкой, стали бы нередактируемыми — открыл,
    /// нажал «Сохранить», получил отказ, и починить из того же экрана нечем.
    /// </summary>
    [Fact]
    public void Кривое_значение_лежавшее_раньше_сохранению_не_мешает()
    {
        var refusals = Guard("""{"Кол":"12,5","Табельный":"7"}""", """{"Кол":"12,5","Табельный":"8"}""");

        Assert.Empty(refusals);
    }

    /// <summary>
    /// Та же запись, но кривое значение ПОПРАВИЛИ на другое кривое — это уже вклад записи, и она
    /// отказывает. Без этой половины предыдущий тест доказывал бы только то, что охрана молчит.
    /// </summary>
    [Fact]
    public void Кривое_значение_заменённое_другим_кривым_отказывает()
    {
        var refusals = Guard("""{"Кол":"12,5"}""", """{"Кол":"13,5"}""");

        Assert.Equal("Кол", Assert.Single(refusals).Path);
    }

    /// <summary>
    /// Вставка строки в таблицу сдвигает пути всех нижних строк. Сравнивай охрана по ПУТИ — старые
    /// кривые значения отказали бы на ровном месте, хотя их никто не трогал.
    /// </summary>
    [Fact]
    public void Вставка_строки_не_делает_старые_значения_новыми()
    {
        var refusals = Guard(
            """{"Работы":[{"Порядок":"2.1"},{"Порядок":"3.1"}]}""",
            """{"Работы":[{"Порядок":1},{"Порядок":"2.1"},{"Порядок":"3.1"}]}""");

        Assert.Empty(refusals);
    }

    [Fact]
    public void Кривое_значение_в_новой_строке_отказывает()
    {
        var refusals = Guard("""{"Работы":[]}""", """{"Работы":[{"Порядок":"2.1"}]}""");

        Assert.Equal("Работы[0].Порядок", Assert.Single(refusals).Path);
    }

    /// <summary>Осиротевший ключ — состояние между правкой схемы и переносом данных, а не отказ.</summary>
    [Fact]
    public void Ключ_вне_схемы_охраной_не_считается()
    {
        Assert.Empty(Guard("""{}""", """{"Лишнее":"что-то"}"""));
    }

    // ── Запертые поля ─────────────────────────────────────────────────────────

    [Fact]
    public void Правка_запертого_поля_отказывает()
    {
        var refusals = Guard("""{"Сумма":100}""", """{"Сумма":200}""");

        var refusal = Assert.Single(refusals);
        Assert.Equal(RecordWriteGuard.LockedField, refusal.Code);
        Assert.Equal("Сумма", refusal.Path);
        Assert.Contains("Сумма", refusal.Message);
    }

    /// <summary>
    /// Молчаливое стирание — самый частый способ испортить запись: клиент, пересобирающий объект
    /// поимённо, теряет свойство, которого не назвал (дефект ревью PR #1004).
    /// </summary>
    [Fact]
    public void Стирание_запертого_поля_отказывает_и_называет_стирание()
    {
        var refusals = Guard("""{"Сумма":100}""", """{}""");

        Assert.Contains("стёрто", Assert.Single(refusals).Message);
    }

    [Fact]
    public void Первое_заполнение_запертого_поля_отказывает()
    {
        var refusals = Guard("""{}""", """{"Сумма":100}""");

        Assert.Contains("впервые", Assert.Single(refusals).Message);
    }

    [Fact]
    public void Запертое_поле_оставленное_как_есть_сохранению_не_мешает()
    {
        Assert.Empty(Guard("""{"Сумма":100,"Кол":1}""", """{"Сумма":100,"Кол":2}"""));
    }

    [Fact]
    public void Запертое_поле_внутри_составного_охраняется_тоже()
    {
        var refusals = Guard("""{"Шапка":{"Итог":10}}""", """{"Шапка":{"Итог":11}}""");

        Assert.Equal("Шапка.Итог", Assert.Single(refusals).Path);
    }

    /// <summary>Поле модуля, которое человек заполняет руками, замком не считается.</summary>
    [Fact]
    public void Поле_модуля_без_замка_правится_свободно()
    {
        Assert.Empty(Guard("""{"Табельный":"7"}""", """{"Табельный":"8"}"""));
    }

    // ── Область действия ──────────────────────────────────────────────────────

    /// <summary>
    /// По букве ТЗ CORE-20 охраняются «расширяемый» и «закрытый». Открытый тип — все сегодняшние
    /// типы заказчика; охрана в них не срабатывает вовсе, и это записано в решении, а не забыто.
    /// </summary>
    [Fact]
    public void В_открытом_типе_охраны_нет()
    {
        Assert.Empty(Guard("""{"Сумма":100}""", """{"Сумма":200,"Кол":"12,5"}""", SchemaEditLevel.Open));
    }

    /// <summary>
    /// ⚠️ Иначе замок снимался бы наследованием: производный тип заводит администратор, и он всегда
    /// открытый — а поля родителя-модуля вместе с замками в нём остаются.
    /// </summary>
    [Fact]
    public void Наследник_открытого_уровня_охраняется_по_родителю()
    {
        var parent = Guid.Parse("a0000000-0000-0000-0000-00000000000f");
        var refusals = RecordWriteGuard.Refusals(
            J("""{"Сумма":100}"""), J("""{"Сумма":200}"""), RecId,
            Types(SchemaEditLevel.Closed, parent), Prims());

        Assert.Equal(RecordWriteGuard.LockedField, Assert.Single(refusals).Code);
    }
}
