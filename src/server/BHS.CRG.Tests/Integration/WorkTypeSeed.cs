using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Справочник единиц и классификатор видов работ — как их получает установка: единицы заводит человек,
/// классификатор проецирует ядро своим объявлением (<see cref="CoreRecordTypes.WorkType" />). Общий для
/// тестов двери видов работ и портов справочников (plan-03).
/// </summary>
internal sealed class WorkTypeSeed(IntegrationTestFixture fixture)
{
    public async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    /// <summary>Тип по коду с собственной схемой — как его завёл бы человек.</summary>
    public async Task<DocumentType> TypeAsync(string code, string name, string schema = """{"fields":[]}""",
        Guid? parentId = null)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var type = DocumentType.Create(name, code, DocumentTypeKind.Composite, parentId,
            JsonDocument.Parse(schema), TypeOwner.Core, TypeVisibility.Shared);
        await repo.AddAsync(type);
        await repo.SaveChangesAsync();
        return type;
    }

    /// <summary>Справочник единиц и классификатор от объявления ядра.</summary>
    public async Task<(DocumentType Unit, DocumentType WorkType)> ClassifierAsync()
    {
        var unit = await TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
        var workType = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType));
        return (unit, workType!);
    }

    public Task<DomainObject> RecordAsync(Guid typeId, string name, string data = "{}") =>
        SendAsync(new CreateCommonDataEntryCommand(name, typeId, JsonDocument.Parse(data), CatalogScope.System, null));

    /// <summary>Данные вида работы с ключами объявления ядра.</summary>
    public static string WorkTypeData(string code, string name, DomainObject unit, bool requiresLocation = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Код"] = code,
            ["Наименование"] = name,
            ["ЕдиницаИзмерения"] = Ref(unit),
            ["ТребуетМесто"] = requiresLocation,
        });

    /// <summary>Ссылка на запись общих данных — как её кладёт форма.</summary>
    public static Dictionary<string, object?> Ref(DomainObject entry) => new()
    {
        ["$ref"] = "catalog", ["entryId"] = entry.Id.ToString(), ["displayName"] = entry.DisplayName,
    };

    public Task ArchiveAsync(Guid id) => SendAsync(new SetRecordArchiveCommand(id, true));
}
