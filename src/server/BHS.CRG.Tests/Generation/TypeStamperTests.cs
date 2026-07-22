using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Штамп метаполя типа объекта (issue #342): data._type + составные объекты по объявленной схеме,
/// иерархия chain (self→root), пропуск пустых/уже-штампованных/$ref.
/// </summary>
public class TypeStamperTests
{
    private static readonly Guid DocId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid PodrId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid WorkId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static DocumentType T(Guid id, string name, string code, Guid? parent, string fieldsJson = "[]") =>
        DocumentType.Restore(id, name, code, DocumentTypeKind.Composite, parent,
            JsonDocument.Parse($"{{\"fields\":{fieldsJson}}}"), JsonDocument.Parse("{}"), false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<Guid, DocumentType> ById(params DocumentType[] ts) => ts.ToDictionary(t => t.Id);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Схема документа: complex «Орг» (ORG) + array «Работы» (WORK) + пустой complex «Пусто» (ORG).
    private static readonly string DocFields =
        $"[{{\"key\":\"Орг\",\"type\":\"complex\",\"typeId\":\"{OrgId}\"}}," +
        $"{{\"key\":\"Работы\",\"type\":\"array\",\"typeId\":\"{WorkId}\"}}," +
        $"{{\"key\":\"Пусто\",\"type\":\"complex\",\"typeId\":\"{OrgId}\"}}]";

    private static (GenerationContext ctx, IReadOnlyDictionary<Guid, DocumentType> byId) Setup()
    {
        var byId = ById(
            T(DocId, "АОСР", "AOSR", null, DocFields),
            T(OrgId, "Организация", "ORG", null),
            T(PodrId, "Подрядчик", "PODR", OrgId),   // потомок ORG — для chain
            T(WorkId, "Работа", "WORK", null));
        var ctx = new GenerationContext();
        ctx.Set("Орг", J("{\"Наименование\":\"ООО Ромашка\"}"));
        ctx.Set("Работы", J("[{\"Название\":\"копать\"},{}]"));  // 2-й элемент пустой
        ctx.Set("Пусто", J("{}"));                                 // пустой составной
        ctx.Set("Номер", J("\"ЭОМ-1\""));                          // скаляр — не трогаем
        return (ctx, byId);
    }

    private static JsonElement Get(GenerationContext ctx, string key) => (JsonElement)ctx.Data[key]!;

    [Fact]
    public void AncestorCodes_SelfFirst_ToRoot()
    {
        var byId = ById(T(OrgId, "Организация", "ORG", null), T(PodrId, "Подрядчик", "PODR", OrgId));
        Assert.Equal(new[] { "PODR", "ORG" }, TypeMeta.AncestorCodes(PodrId, byId));
        Assert.Equal(new[] { "ORG" }, TypeMeta.AncestorCodes(OrgId, byId));
    }

    [Fact]
    public void Stamp_DocumentRoot_CodeNameChain()
    {
        var (ctx, byId) = Setup();
        TypeStamper.Stamp(ctx, DocId, byId);
        var t = Get(ctx, "_type");
        Assert.Equal("AOSR", t.GetProperty("code").GetString());
        Assert.Equal("АОСР", t.GetProperty("name").GetString());
        Assert.Equal(new[] { "AOSR" }, t.GetProperty("chain").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Stamp_InlineComposite_GetsDeclaredType()
    {
        var (ctx, byId) = Setup();
        TypeStamper.Stamp(ctx, DocId, byId);
        var org = Get(ctx, "Орг");
        Assert.Equal("ORG", org.GetProperty("_type").GetProperty("code").GetString());
        Assert.Equal("ООО Ромашка", org.GetProperty("Наименование").GetString()); // данные сохранены
    }

    [Fact]
    public void Stamp_ArrayItems_NonEmptyOnly()
    {
        var (ctx, byId) = Setup();
        TypeStamper.Stamp(ctx, DocId, byId);
        var arr = Get(ctx, "Работы").EnumerateArray().ToList();
        Assert.Equal("WORK", arr[0].GetProperty("_type").GetProperty("code").GetString());
        Assert.False(arr[1].TryGetProperty("_type", out _)); // пустой элемент НЕ штампуется
    }

    [Fact]
    public void Stamp_EmptyComposite_NotStamped()
    {
        var (ctx, byId) = Setup();
        TypeStamper.Stamp(ctx, DocId, byId);
        Assert.False(Get(ctx, "Пусто").TryGetProperty("_type", out _));
    }

    [Fact]
    public void Stamp_Scalar_Untouched()
    {
        var (ctx, byId) = Setup();
        TypeStamper.Stamp(ctx, DocId, byId);
        Assert.Equal("ЭОМ-1", Get(ctx, "Номер").GetString());
    }

    [Fact]
    public void Stamp_Ref_UsesActualTypeIdFromResolver_ChainToRoot()
    {
        // Поле объявлено «Организация», а резолвер пометил запись фактическим типом «Подрядчик» (_typeId).
        // Штамп берёт фактический тип: code=PODR, chain=[PODR,ORG]. Сырой _typeId в data.json не течёт.
        var byId = ById(
            T(DocId, "АОСР", "AOSR", null, $"[{{\"key\":\"Орг\",\"type\":\"doc-ref\",\"typeId\":\"{OrgId}\"}}]"),
            T(OrgId, "Организация", "ORG", null),
            T(PodrId, "Подрядчик", "PODR", OrgId));
        var ctx = new GenerationContext();
        ctx.Set("Орг", J($"{{\"Наименование\":\"ООО Подряд\",\"_typeId\":\"{PodrId}\"}}"));
        TypeStamper.Stamp(ctx, DocId, byId);
        var org = Get(ctx, "Орг");
        Assert.Equal("PODR", org.GetProperty("_type").GetProperty("code").GetString());
        Assert.Equal(new[] { "PODR", "ORG" }, org.GetProperty("_type").GetProperty("chain").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(org.TryGetProperty("_typeId", out _)); // сырой маркер удалён
        Assert.Equal("ООО Подряд", org.GetProperty("Наименование").GetString());
    }

    [Fact]
    public void Stamp_SkipsRefAndAlreadyStamped()
    {
        var byId = ById(T(DocId, "АОСР", "AOSR", null,
            $"[{{\"key\":\"A\",\"type\":\"complex\",\"typeId\":\"{OrgId}\"}}," +
            $"{{\"key\":\"B\",\"type\":\"complex\",\"typeId\":\"{OrgId}\"}}]"), T(OrgId, "Организация", "ORG", null));
        var ctx = new GenerationContext();
        ctx.Set("A", J("{\"$ref\":\"catalog\",\"entryId\":\"x\"}"));            // неразрешённая ссылка
        ctx.Set("B", J("{\"Наименование\":\"Y\",\"_type\":{\"code\":\"PODR\"}}")); // уже штампован (реф, фаза 2)
        TypeStamper.Stamp(ctx, DocId, byId);
        Assert.True(Get(ctx, "A").TryGetProperty("$ref", out _));
        Assert.False(Get(ctx, "A").TryGetProperty("_type", out _));            // $ref не штампуем
        Assert.Equal("PODR", Get(ctx, "B").GetProperty("_type").GetProperty("code").GetString()); // не перезаписан
    }
}
