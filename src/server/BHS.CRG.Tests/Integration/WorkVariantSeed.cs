using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Классификатор с двумя видами работ и справочник вариантов — как их получает установка: единицы
/// заводит человек, виды работ и варианты проецирует ядро своими объявлениями (work-06).
/// </summary>
internal sealed class WorkVariantSeed(IntegrationTestFixture fixture)
{
    public WorkTypeSeed Types { get; } = new(fixture);

    /// <summary>Справочник вариантов и два вида работ: «Прокладка кабеля» и «Прокладка провода».</summary>
    public async Task<(DocumentType VariantType, DomainObject Cable, DomainObject Wire)> SetUpAsync()
    {
        var (unit, workType) = await Types.ClassifierAsync();
        var variantType = (await Types.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkVariant)))!;
        var metre = await Types.RecordAsync(unit.Id, "м");
        var cable = await Types.RecordAsync(workType.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", metre));
        var wire = await Types.RecordAsync(workType.Id, "Прокладка провода", WorkTypeSeed.WorkTypeData("01.03", "Прокладка провода", metre));
        return (variantType, cable, wire);
    }

    /// <summary>Вариант, заведённый командой — мимо двери, как его кладёт подготовка теста.</summary>
    /// <param name="createAnyway">Завести рядом с архивным тёзкой: общая проверка ключа ссылку на вид не видит.</param>
    public Task<DomainObject> VariantAsync(DocumentType variantType, DomainObject workType, string name, bool createAnyway = false) =>
        Types.SendAsync(new CreateCommonDataEntryCommand(
            name, variantType.Id, JsonDocument.Parse(Data(workType, name)), CatalogScope.System, null, CreateAnyway: createAnyway));

    /// <summary>Данные варианта с ключами объявления ядра.</summary>
    public static string Data(DomainObject workType, string name) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [CoreRecordTypes.WorkVariantWorkTypeKey] = WorkTypeSeed.Ref(workType),
            [CoreRecordTypes.WorkVariantNameKey] = name,
        });
}
