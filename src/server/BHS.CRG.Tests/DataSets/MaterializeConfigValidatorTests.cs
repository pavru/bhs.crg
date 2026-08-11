using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Проверка настройки материализации перед сохранением (issue #716).
///
/// Противоречивое правило не падает при сохранении и не видно в диалоге: оно проявляется молчаливо
/// пропущенными строками через месяц, в готовом реестре, где недостача не бросается в глаза. Каждая
/// проверка здесь — про такой случай, а не про формальную валидность JSON.
/// </summary>
public class MaterializeConfigValidatorTests
{
    private static readonly Guid AosrTypeId = Guid.NewGuid();
    private static readonly Guid RegistryTypeId = Guid.NewGuid();
    private static readonly Guid UnionId = Guid.NewGuid();
    private static readonly Guid PlainId = Guid.NewGuid();

    private static DocumentType Type(Guid id, string name, string schema, DocumentTypeKind kind = DocumentTypeKind.Composite)
        => DocumentType.Restore(id, name, $"C{id:N}"[..8], kind, null,
            JsonDocument.Parse(schema.Replace('\'', '"')), JsonDocument.Parse("{}"),
            false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false);

    private static readonly Dictionary<Guid, DocumentType> Types = new()
    {
        [UnionId] = Type(UnionId, "Документ комплекта",
            "{'tags':['type.union'],'fields':[{'key':'АОСР','type':'doc-ref'},{'key':'Реестр','type':'doc-ref'}]}"),
        [PlainId] = Type(PlainId, "Строка реестра",
            "{'fields':[{'key':'Наименование','type':'string'},{'key':'Номер','type':'string'}]}"),
        [AosrTypeId] = Type(AosrTypeId, "АОСР", "{'fields':[]}", DocumentTypeKind.Document),
        [RegistryTypeId] = Type(RegistryTypeId, "Реестр работ", "{'fields':[]}", DocumentTypeKind.Document),
    };

    private static void Validate(Guid typeId, Dictionary<string, string> mapping,
        MaterializeDiscriminatorConfig? d = null, string? byIdColumn = null)
        => MaterializeConfigValidator.Validate(Types[typeId], mapping, d, Types, byIdColumn);

    private static MaterializeDiscriminatorConfig Discriminator(Dictionary<string, List<Guid>> rules)
        => new("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode, rules);

    /// <summary>Обычный тип: полей много, все заполняются одной строкой — проверять нечего.</summary>
    [Fact]
    public void PlainType_ManyKeys_IsFine()
        => Validate(PlainId, new() { ["Наименование"] = "A", ["Номер"] = "B" });

    /// <summary>Прежняя настройка union'а (ровно один вариант, правила нет) остаётся законной —
    /// обратная совместимость здесь не декларация, а проверка.</summary>
    [Fact]
    public void Union_SingleVariantWithoutDiscriminator_IsFine()
        => Validate(UnionId, new() { ["АОСР"] = "Ид" });

    /// <summary>
    /// Несколько вариантов без правила — экземпляр, в котором заполнено всё сразу, то есть не union.
    /// Раньше это молча сохранялось.
    /// </summary>
    [Fact]
    public void Union_ManyVariantsWithoutDiscriminator_IsRejected()
    {
        var ex = Assert.Throws<InvalidRequestException>(
            () => Validate(UnionId, new() { ["АОСР"] = "Ид", ["Реестр"] = "Ид" }));
        Assert.Contains("одним вариантом", ex.Message);
    }

    [Fact]
    public void Union_ManyVariantsWithDiscriminator_IsFine()
        => Validate(UnionId, new() { ["АОСР"] = "Ид", ["Реестр"] = "Ид" },
            Discriminator(new() { ["АОСР"] = [AosrTypeId], ["Реестр"] = [RegistryTypeId] }));

    /// <summary>Вариант без правила выключен — законно: реестр может собирать не все виды документов.</summary>
    [Fact]
    public void Union_VariantWithoutRule_IsDisabledNotInvalid()
        => Validate(UnionId, new() { ["АОСР"] = "Ид" }, Discriminator(new() { ["АОСР"] = [AosrTypeId] }));

    /// <summary>
    /// Типы назначены, а маппинга нет — строки этих типов дали бы пустой объект. Ровно та молчаливая
    /// пустота, ради которой заведён issue #715, только этажом выше.
    /// </summary>
    [Fact]
    public void Union_RuleWithoutMapping_IsRejected()
    {
        var ex = Assert.Throws<InvalidRequestException>(
            () => Validate(UnionId, new() { ["АОСР"] = "Ид" },
                Discriminator(new() { ["АОСР"] = [AosrTypeId], ["Реестр"] = [RegistryTypeId] })));
        Assert.Contains("не задан маппинг", ex.Message);
    }

    /// <summary>Один тип у двух вариантов: «кто первый» решал бы порядок ключей в JSON, которого
    /// никто не задумывал.</summary>
    [Fact]
    public void Union_SameTypeInTwoVariants_IsRejected()
    {
        var ex = Assert.Throws<InvalidRequestException>(
            () => Validate(UnionId, new() { ["АОСР"] = "Ид", ["Реестр"] = "Ид" },
                Discriminator(new() { ["АОСР"] = [AosrTypeId], ["Реестр"] = [AosrTypeId] })));
        Assert.Contains("двум вариантам", ex.Message);
    }

    [Fact]
    public void Union_MappingOfUnknownVariant_IsRejected()
        => Assert.Throws<InvalidRequestException>(() => Validate(UnionId, new() { ["Чужое"] = "Ид" }));

    [Fact]
    public void Union_RuleForUnknownVariant_IsRejected()
        => Assert.Throws<InvalidRequestException>(
            () => Validate(UnionId, new() { ["АОСР"] = "Ид" },
                Discriminator(new() { ["АОСР"] = [AosrTypeId], ["Чужое"] = [RegistryTypeId] })));

    /// <summary>У обычного типа выбирать не из чего — правило там бессмысленно и принято быть не должно.</summary>
    [Fact]
    public void PlainType_WithDiscriminator_IsRejected()
        => Assert.Throws<InvalidRequestException>(
            () => Validate(PlainId, new() { ["Наименование"] = "A" },
                Discriminator(new() { ["Наименование"] = [AosrTypeId] })));

    // ── Режим «существующий документ по Ид» (issue #725) ──────────────────────────

    /// <summary>Тип-документ + колонка с Ид — законная настройка: строка целиком станет ссылкой.</summary>
    [Fact]
    public void Document_ByIdColumn_IsFine()
        => Validate(AosrTypeId, new(), byIdColumn: "Ид");

    /// <summary>
    /// Составной тип по Ид не адресуется: экземпляров-документов у него нет, и ссылка на «строку
    /// реестра» не значила бы ничего — резолвер оставил бы её висеть.
    /// </summary>
    [Fact]
    public void CompositeType_ByIdColumn_IsRejected()
    {
        var ex = Assert.Throws<InvalidRequestException>(() => Validate(PlainId, new(), byIdColumn: "Ид"));
        Assert.Contains("не является типом документа", ex.Message);
    }

    /// <summary>
    /// Маппинг и режим «по Ид» — два разных ответа на вопрос «что такое строка». Сохранив оба, мы
    /// оставили бы настройку, в которой видимое в диалоге не совпадает с тем, что уедет в документ.
    /// </summary>
    [Fact]
    public void ByIdColumn_WithMapping_IsRejected()
    {
        var ex = Assert.Throws<InvalidRequestException>(
            () => Validate(AosrTypeId, new() { ["Номер"] = "A" }, byIdColumn: "Ид"));
        Assert.Contains("маппинг колонок в нём не задаётся", ex.Message);
    }

    /// <summary>Пустые значения маппинга — это тот же пустой маппинг, и режиму они не мешают
    /// (то же определение «пусто», по которому маппинг вообще выбирается).</summary>
    [Fact]
    public void ByIdColumn_WithEmptyMappingValues_IsFine()
        => Validate(AosrTypeId, new() { ["Номер"] = "" }, byIdColumn: "Ид");

    [Fact]
    public void ByIdColumn_WithDiscriminator_IsRejected()
        => Assert.Throws<InvalidRequestException>(
            () => Validate(AosrTypeId, new(), Discriminator(new() { ["АОСР"] = [AosrTypeId] }), byIdColumn: "Ид"));

    [Fact]
    public void Discriminator_WithoutColumn_IsRejected()
        => Assert.Throws<InvalidRequestException>(
            () => Validate(UnionId, new() { ["АОСР"] = "Ид" },
                new MaterializeDiscriminatorConfig("  ", MaterializeDiscriminatorConfig.ByTypeCode,
                    new() { ["АОСР"] = [AosrTypeId] })));
}
