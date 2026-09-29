using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Типы ядра для модуля: идентификатор по коду (задача C1 этапа 2, issue #1076).
///
/// <para>Прямым чтением набора, а не через MediatR — в отличие от
/// <see cref="ModuleCatalogPort" />, и разница здесь по существу. У справочников есть обработчики, в
/// которых завтра появится то, что модулю обязательно тоже; у «найди тип по коду» обработчика нет
/// вовсе, и заводить его ради порта значило бы завести второй путь чтения типов — а типы читают
/// напрямую девять мест ядра, и десятое ничего не добавит.</para>
///
/// <para>⚠️ Ищем по КОДУ, а не по имени: имя администратор переименовывает («Счёт на оплату» → «Счёт
/// поставщика»), а код постоянен — им же адресуются печатные блоки (ТЗ TYPE-6).</para>
/// </summary>
public sealed class ModuleTypesPort(IRepository<DocumentType> types) : IModuleTypes
{
    public async Task<Guid?> FindAsync(string code, CancellationToken ct = default)
    {
        var found = await types.FindAsync(t => t.Code == code, ct);
        return found.Count > 0 ? found[0].Id : null;
    }
}
