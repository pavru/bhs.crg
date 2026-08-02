using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Аудитор данных инстанса против эффективной схемы (issue #348): осиротевшие ключи + несовпадение
/// вида + тонкий слой соответствия значения типу (issue #642).
/// </summary>
public class SchemaDataAuditorTests
{
    private static readonly Guid DocId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid IntId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static DocumentType T(Guid id, string name, string code, Guid? parent, string fieldsJson) =>
        DocumentType.Restore(id, name, code, DocumentTypeKind.Composite, parent,
            JsonDocument.Parse($"{{\"fields\":{fieldsJson}}}"), JsonDocument.Parse("{}"), false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<Guid, DocumentType> ById(params DocumentType[] ts) => ts.ToDictionary(t => t.Id);
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Примитив «Цело число» — тот самый тип из #461, на котором нашли строки «2.1».</summary>
    private static IReadOnlyDictionary<Guid, PrimitiveType> Prims() => new Dictionary<Guid, PrimitiveType>
    {
        [IntId] = PrimitiveType.Restore(IntId, "Цело число", "int", "number", null,
            JsonDocument.Parse("{\"integer\":true}"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    };

    private static IReadOnlyDictionary<Guid, DocumentType> Types() => ById(
        T(DocId, "АОСР", "AOSR", null,
            $"[{{\"key\":\"Работы\",\"type\":\"array\",\"typeId\":\"{WorkId}\"}}," +
            "{\"key\":\"Номер\",\"type\":\"string\"}]"),
        T(WorkId, "Работа", "WORK", null, "[{\"key\":\"Наименование\",\"type\":\"string\"}]"));

    [Fact]
    public void Audit_FlagsOrphanTopLevelKey()
    {
        var data = J("{\"Номер\":\"1\",\"НовыеРаботы\":{\"x\":1}}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types(), Prims());
        var orphan = Assert.Single(issues, i => i.Code == SchemaDataAuditor.OrphanKey);
        Assert.Equal("НовыеРаботы", orphan.Path);
        Assert.Equal(AuditSeverity.Warning, orphan.Severity);
    }

    [Fact]
    public void Audit_FlagsOrphanNestedKey_InArrayItem()
    {
        // Осиротевший ключ ВНУТРИ элемента массива (по схеме подтипа Работа) — рекурсивно, путь Работы[0].Лишнее.
        var data = J("{\"Работы\":[{\"Наименование\":\"a\",\"Лишнее\":5}]}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types(), Prims());
        Assert.Contains(issues, i => i.Code == SchemaDataAuditor.OrphanKey && i.Path == "Работы[0].Лишнее");
    }

    [Fact]
    public void Audit_IgnoresMetaKeys()
    {
        var data = J("{\"Номер\":\"1\",\"_type\":{\"code\":\"AOSR\"},\"_baseRef\":\"x\"}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types(), Prims());
        Assert.DoesNotContain(issues, i => i.Path is "_type" or "_baseRef");
    }

    [Fact]
    public void Audit_FlagsTypeMismatch_ArrayFieldHoldsScalar()
    {
        var data = J("{\"Работы\":\"строка\"}"); // массив ожидается, строка в данных
        var issues = SchemaDataAuditor.Audit(data, DocId, Types(), Prims());
        Assert.Contains(issues, i => i.Code == SchemaDataAuditor.TypeMismatch && i.Path == "Работы");
    }

    [Fact]
    public void Audit_CleanData_NoIssues()
    {
        var data = J("{\"Номер\":\"1\",\"Работы\":[{\"Наименование\":\"a\"}]}");
        Assert.Empty(SchemaDataAuditor.Audit(data, DocId, Types(), Prims()));
    }

    [Fact]
    public void Audit_DoesNotDescendIntoRefObjects()
    {
        // $ref-объект в составном поле — ссылка, вглубь не идём (его ключи не осиротевшие).
        var byId = ById(
            T(DocId, "T", "T", null, $"[{{\"key\":\"Орг\",\"type\":\"complex\",\"typeId\":\"{WorkId}\"}}]"),
            T(WorkId, "W", "W", null, "[{\"key\":\"Наименование\",\"type\":\"string\"}]"));
        var data = J("{\"Орг\":{\"$ref\":\"catalog\",\"entryId\":\"x\",\"ПостороннийКлюч\":1}}");
        Assert.Empty(SchemaDataAuditor.Audit(data, DocId, byId, Prims()));
    }

    // ── Тонкий слой: значение против объявленного типа (issue #642) ────────────────────────────
    // До этого аудит видел только грубое несовпадение ВИДА (объект вместо скаляра). Всё, ради чего
    // писали ValueTypeScanner — строка в числовом поле, дробь в целом, — проходило мимо, и записи
    // общих данных (единственные объекты вне пайплайна генерации) не проверялись вовсе.

    private static IReadOnlyDictionary<Guid, DocumentType> TypedTypes() => ById(
        T(DocId, "Запись", "REC", null,
            $"[{{\"key\":\"Порядок\",\"type\":\"primitive\",\"typeId\":\"{IntId}\",\"title\":\"Порядковый номер\"}}," +
            "{\"key\":\"Кол\",\"type\":\"number\"}," +
            "{\"key\":\"Флаг\",\"type\":\"boolean\"}," +
            $"{{\"key\":\"Работы\",\"type\":\"array\",\"typeId\":\"{WorkId}\"}}]"),
        T(WorkId, "Работа", "WORK", null,
            $"[{{\"key\":\"Порядок\",\"type\":\"primitive\",\"typeId\":\"{IntId}\"}}]"));

    [Fact]
    public void Audit_FlagsStringInNumberField()
    {
        var issues = SchemaDataAuditor.Audit(J("{\"Кол\":\"12,5\"}"), DocId, TypedTypes(), Prims());
        var issue = Assert.Single(issues);
        Assert.Equal(SchemaDataAuditor.ValueType, issue.Code);
        Assert.Equal("Кол", issue.Path);
        Assert.Contains("ожидается число", issue.Message);
    }

    [Fact]
    public void Audit_FlagsFractionInIntegerPrimitive_ByTitle()
    {
        var issues = SchemaDataAuditor.Audit(J("{\"Порядок\":2.1}"), DocId, TypedTypes(), Prims());
        var issue = Assert.Single(issues);
        Assert.Equal(SchemaDataAuditor.ValueType, issue.Code);
        // Человеку показываем заголовок поля, а не ключ схемы — ключей он не видел никогда.
        Assert.Contains("Порядковый номер", issue.Message);
    }

    [Fact]
    public void Audit_FlagsValueTypeInsideArrayRow()
    {
        // Ровно случай #461: расхождение лежит внутри строки таблицы, на верхнем уровне его нет.
        var issues = SchemaDataAuditor.Audit(J("{\"Работы\":[{\"Порядок\":\"2.1\"}]}"), DocId, TypedTypes(), Prims());
        var issue = Assert.Single(issues);
        Assert.Equal("Работы[0].Порядок", issue.Path);
    }

    [Fact]
    public void Audit_FlagsStringInBooleanField()
    {
        // «да» вместо true: поля-флажки до вынесения правил не проверялись ни одним из двух путей.
        var issues = SchemaDataAuditor.Audit(J("{\"Флаг\":\"да\"}"), DocId, TypedTypes(), Prims());
        Assert.Contains(issues, i => i.Code == SchemaDataAuditor.ValueType && i.Path == "Флаг");
    }

    [Fact]
    public void Audit_ObjectInScalarField_ReportsMismatchOnce()
    {
        // Вид не сошёлся — грубая находка уже всё сказала; тонкий слой не должен повторять её вторым
        // сообщением о том же месте.
        var issues = SchemaDataAuditor.Audit(J("{\"Кол\":{\"a\":1}}"), DocId, TypedTypes(), Prims());
        var issue = Assert.Single(issues);
        Assert.Equal(SchemaDataAuditor.TypeMismatch, issue.Code);
    }

    [Fact]
    public void Audit_TypedValues_NoIssues()
    {
        var data = J("{\"Кол\":12.5,\"Порядок\":3,\"Флаг\":true,\"Работы\":[{\"Порядок\":1}]}");
        Assert.Empty(SchemaDataAuditor.Audit(data, DocId, TypedTypes(), Prims()));
    }

    [Fact]
    public void Audit_WithoutPrimitives_SkipsPrimitiveFields()
    {
        // Пустой словарь примитивов — правила примитива неизвестны, и придумывать их нечем.
        var empty = new Dictionary<Guid, PrimitiveType>();
        Assert.Empty(SchemaDataAuditor.Audit(J("{\"Порядок\":\"2.1\"}"), DocId, TypedTypes(), empty));
    }
}
