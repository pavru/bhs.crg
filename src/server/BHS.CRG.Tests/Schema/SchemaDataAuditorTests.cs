using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>Аудитор данных инстанса против эффективной схемы (issue #348): осиротевшие ключи + несовпадение вида.</summary>
public class SchemaDataAuditorTests
{
    private static readonly Guid DocId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static DocumentType T(Guid id, string name, string code, Guid? parent, string fieldsJson) =>
        DocumentType.Restore(id, name, code, DocumentTypeKind.Composite, parent,
            JsonDocument.Parse($"{{\"fields\":{fieldsJson}}}"), JsonDocument.Parse("{}"), false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<Guid, DocumentType> ById(params DocumentType[] ts) => ts.ToDictionary(t => t.Id);
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyDictionary<Guid, DocumentType> Types() => ById(
        T(DocId, "АОСР", "AOSR", null,
            $"[{{\"key\":\"Работы\",\"type\":\"array\",\"typeId\":\"{WorkId}\"}}," +
            "{\"key\":\"Номер\",\"type\":\"string\"}]"),
        T(WorkId, "Работа", "WORK", null, "[{\"key\":\"Наименование\",\"type\":\"string\"}]"));

    [Fact]
    public void Audit_FlagsOrphanTopLevelKey()
    {
        var data = J("{\"Номер\":\"1\",\"НовыеРаботы\":{\"x\":1}}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types());
        var orphan = Assert.Single(issues, i => i.Code == SchemaDataAuditor.OrphanKey);
        Assert.Equal("НовыеРаботы", orphan.Path);
        Assert.Equal(AuditSeverity.Warning, orphan.Severity);
    }

    [Fact]
    public void Audit_FlagsOrphanNestedKey_InArrayItem()
    {
        // Осиротевший ключ ВНУТРИ элемента массива (по схеме подтипа Работа) — рекурсивно, путь Работы[0].Лишнее.
        var data = J("{\"Работы\":[{\"Наименование\":\"a\",\"Лишнее\":5}]}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types());
        Assert.Contains(issues, i => i.Code == SchemaDataAuditor.OrphanKey && i.Path == "Работы[0].Лишнее");
    }

    [Fact]
    public void Audit_IgnoresMetaKeys()
    {
        var data = J("{\"Номер\":\"1\",\"_type\":{\"code\":\"AOSR\"},\"_baseRef\":\"x\"}");
        var issues = SchemaDataAuditor.Audit(data, DocId, Types());
        Assert.DoesNotContain(issues, i => i.Path is "_type" or "_baseRef");
    }

    [Fact]
    public void Audit_FlagsTypeMismatch_ArrayFieldHoldsScalar()
    {
        var data = J("{\"Работы\":\"строка\"}"); // массив ожидается, строка в данных
        var issues = SchemaDataAuditor.Audit(data, DocId, Types());
        Assert.Contains(issues, i => i.Code == SchemaDataAuditor.TypeMismatch && i.Path == "Работы");
    }

    [Fact]
    public void Audit_CleanData_NoIssues()
    {
        var data = J("{\"Номер\":\"1\",\"Работы\":[{\"Наименование\":\"a\"}]}");
        Assert.Empty(SchemaDataAuditor.Audit(data, DocId, Types()));
    }

    [Fact]
    public void Audit_DoesNotDescendIntoRefObjects()
    {
        // $ref-объект в составном поле — ссылка, вглубь не идём (его ключи не осиротевшие).
        var byId = ById(
            T(DocId, "T", "T", null, $"[{{\"key\":\"Орг\",\"type\":\"complex\",\"typeId\":\"{WorkId}\"}}]"),
            T(WorkId, "W", "W", null, "[{\"key\":\"Наименование\",\"type\":\"string\"}]"));
        var data = J("{\"Орг\":{\"$ref\":\"catalog\",\"entryId\":\"x\",\"ПостороннийКлюч\":1}}");
        Assert.Empty(SchemaDataAuditor.Audit(data, DocId, byId));
    }
}
