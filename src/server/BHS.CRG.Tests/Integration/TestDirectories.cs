using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Справочники ядра в тестовой базе (issue #1101).
///
/// <para>Код находит организации и номенклатуру по РОЛИ, а не по коду типа, — поэтому тесту мало
/// завести тип «Организация»: его надо назначить справочником. Назначает то же правило, которым
/// роли получают действующие установки (<see cref="DirectoryRoleDefaults" />): тип с прежним кодом
/// получает роль, его поля «ИНН» и «Наименование» — тэги. Так тестовая база выглядит как база
/// заказчика после обновления, а не как выдуманная.</para>
///
/// <para>Коды здесь — коды ТИПОВ тестовой базы, а не адреса: модуль их не знает.</para>
/// </summary>
internal static class TestDirectories
{
    public const string OrganizationCode = "Организация";
    public const string NomenclatureCode = "Номенклатура";

    /// <summary>Назначить справочники по прежним именам типов. Повторный вызов ничего не меняет.</summary>
    public static async Task AssignRolesAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IRepository<DocumentType>>();
        var changed = DirectoryRoleDefaults.Apply(await types.GetAllAsync());
        if (changed.Count == 0) return;

        foreach (var (type, schema) in changed)
        {
            type.UpdateSchema(schema);
            types.Update(type);
        }
        await types.SaveChangesAsync();
    }
}
