using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Выбор варианта union'а по типу документа строки (issue #716).
///
/// Проверяется не «работает ли словарь», а правило специфичности: правило варианта называет тип, и
/// подходит любой его потомок, поэтому одна строка законно подходит нескольким вариантам сразу.
/// Побеждать обязан ближайший по наследованию — иначе общий предок в одном варианте перебивал бы
/// точное совпадение в другом, и реестр раскладывался бы «как получилось».
/// </summary>
public class MaterializeVariantSelectorTests
{
    private static readonly Guid AosrId = Guid.NewGuid();
    private static readonly Guid AosrElectricId = Guid.NewGuid();
    private static readonly Guid RegistryId = Guid.NewGuid();

    private static DocumentType Type(Guid id, string name, string code, Guid? parentId = null)
        => DocumentType.Restore(id, name, code, DocumentTypeKind.Document, parentId,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
            false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false);

    private static readonly Dictionary<Guid, DocumentType> Types = new()
    {
        [AosrId] = Type(AosrId, "АОСР", "АОСР"),
        [AosrElectricId] = Type(AosrElectricId, "АОСР электромонтаж", "АОСР_ЭМ", AosrId),
        [RegistryId] = Type(RegistryId, "Реестр работ", "РеестрРабот"),
    };

    private static MaterializeVariantSelector Selector(
        Dictionary<string, List<Guid>> rules, string kind = MaterializeDiscriminatorConfig.ByTypeCode,
        Dictionary<Guid, Guid>? typeByDocument = null)
        => MaterializeVariantSelector.Create(
            new MaterializeDiscriminatorConfig("ТипКод", kind, rules), Types, typeByDocument);

    private static Dictionary<string, string?> Row(string? value) => new() { ["ТипКод"] = value };

    [Fact]
    public void ExactCode_PicksItsVariant()
    {
        var choice = Selector(new() { ["АОСР"] = [AosrId], ["Реестр"] = [RegistryId] }).Choose(Row("АОСР"));

        Assert.Equal("АОСР", choice.VariantKey);
        Assert.Null(choice.SkipReason);
    }

    /// <summary>Правило на родительский тип забирает и потомков — иначе каждый подвид пришлось бы
    /// перечислять руками, а появившийся завтра тихо выпал бы из реестра.</summary>
    [Fact]
    public void DescendantType_MatchesParentRule()
        => Assert.Equal("АОСР", Selector(new() { ["АОСР"] = [AosrId] }).Choose(Row("АОСР_ЭМ")).VariantKey);

    /// <summary>
    /// Ближе по наследованию — точнее. Здесь один вариант назван предком, другой — самим типом
    /// строки: побеждает второй. Обратный порядок ключей ничего не меняет — порядок в JSON никто
    /// не задумывал, и опираться на него было бы худшим из решений.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearerInheritance_Wins(bool reversedOrder)
    {
        var rules = reversedOrder
            ? new Dictionary<string, List<Guid>> { ["Точный"] = [AosrElectricId], ["Общий"] = [AosrId] }
            : new Dictionary<string, List<Guid>> { ["Общий"] = [AosrId], ["Точный"] = [AosrElectricId] };

        Assert.Equal("Точный", Selector(rules).Choose(Row("АОСР_ЭМ")).VariantKey);
    }

    /// <summary>
    /// Одинаковая близость у двух вариантов — противоречие настройки. Угадывать нельзя: строка
    /// пропускается, и об этом говорят ошибкой (валидатор такое и не сохранит — это страховка на
    /// случай настройки, сохранённой раньше него).
    /// </summary>
    [Fact]
    public void EquallySpecificRules_AreAmbiguous()
    {
        var choice = Selector(new() { ["Первый"] = [AosrId], ["Второй"] = [AosrId] }).Choose(Row("АОСР"));

        Assert.Null(choice.VariantKey);
        Assert.Equal(MaterializeSkipReason.Ambiguous, choice.SkipReason);
    }

    /// <summary>Вариант без правила выключен: строки его типов пропускаются — это законная настройка
    /// (реестр может собирать не все виды документов), поэтому причина отдельная и внятная.</summary>
    [Fact]
    public void TypeWithoutRule_IsSkippedWithReason()
    {
        var choice = Selector(new() { ["АОСР"] = [AosrId] }).Choose(Row("РеестрРабот"));

        Assert.Null(choice.VariantKey);
        Assert.Equal(MaterializeSkipReason.NoVariant, choice.SkipReason);
    }

    [Theory]
    [InlineData(null, MaterializeSkipReason.EmptyValue)]
    [InlineData("", MaterializeSkipReason.EmptyValue)]
    [InlineData("   ", MaterializeSkipReason.EmptyValue)]
    [InlineData("НетТакогоКода", MaterializeSkipReason.UnknownTypeCode)]
    public void BadDiscriminatorValue_HasItsOwnReason(string? cell, string expected)
        => Assert.Equal(expected, Selector(new() { ["АОСР"] = [AosrId] }).Choose(Row(cell)).SkipReason);

    /// <summary>Признак-идентификатор: тип берётся у документа, а не у колонки.</summary>
    [Fact]
    public void ByDocumentId_UsesPrefetchedTypeOfDocument()
    {
        var documentId = Guid.NewGuid();
        var selector = Selector(new() { ["АОСР"] = [AosrId] }, MaterializeDiscriminatorConfig.ByDocumentId,
            new Dictionary<Guid, Guid> { [documentId] = AosrElectricId });

        Assert.Equal("АОСР", selector.Choose(Row(documentId.ToString())).VariantKey);
    }

    /// <summary>Документ, которого нет (удалён после выгрузки), — своя причина, не «нет варианта».
    /// Разница важна: одно чинится настройкой, другое — данными.</summary>
    [Theory]
    [InlineData("не-идентификатор")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ByDocumentId_MissingDocument_HasItsOwnReason(string cell)
    {
        var selector = Selector(new() { ["АОСР"] = [AosrId] }, MaterializeDiscriminatorConfig.ByDocumentId,
            new Dictionary<Guid, Guid>());

        Assert.Equal(MaterializeSkipReason.DocumentNotFound, selector.Choose(Row(cell)).SkipReason);
    }

    /// <summary>Негодная или отсутствующая конфигурация означает «дискриминатора нет» — материализация
    /// остаётся статичной, а не падает.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"kind":"docTypeCode","rules":{}}""")]
    public void UnusableConfig_ParsesToNull(string? json)
        => Assert.Null(MaterializeVariantSelector.ParseConfig(json));

    [Fact]
    public void DocumentIds_AreCollectedOnlyForIdKind()
    {
        var id = Guid.NewGuid();
        var rows = new List<IReadOnlyDictionary<string, string?>> { Row(id.ToString()), Row("мусор") };

        var byId = new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByDocumentId, []);
        Assert.Equal([id], MaterializeVariantSelector.DocumentIdsIn(byId, rows));

        var byCode = new MaterializeDiscriminatorConfig("ТипКод", MaterializeDiscriminatorConfig.ByTypeCode, []);
        Assert.Empty(MaterializeVariantSelector.DocumentIdsIn(byCode, rows));
    }
}
