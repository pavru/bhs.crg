using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Справочники ядра, записи которых пишутся СВОЕЙ дверью под своим правом, а общим адресом
/// справочников — нет (WORK-59). Реестр, а не условие в двери: вторая такая группа (варианты работ,
/// work-06) — строка здесь, а не копия отказа. Зеркало на клиенте — <c>OWN_DOORS</c> в
/// <c>shared/api/commonData.ts</c>.
///
/// <para>Справочник — по коду КОРНЯ, и закрыта вся его семья: запись подтипа вида работы — тоже вид
/// работы (<see cref="CoreDirectories.Family" />).</para>
/// </summary>
public static class OwnDoorTypes
{
    /// <param name="Plural">Чьи записи — словами отказа: «Виды работ ведутся своим адресом…».</param>
    /// <param name="Right">Право — словами настройки ролей, как его видит администратор.</param>
    public sealed record OwnDoor(string TypeCode, string Door, string Permission, string Plural, string Right);

    public static readonly IReadOnlyList<OwnDoor> All =
    [
        new(CoreRecordTypes.WorkTypeCode, WorkTypeEndpoints.Door, CorePermissions.WorkTypesEdit,
            "Виды работ", "вести классификатор видов работ"),
    ];

    /// <summary>
    /// Отказ общего адреса записи справочника со своей дверью — или <c>null</c>, если запись не из
    /// него. Общий адрес зовёт его на заведении (по типу), правке, удалении и отправке в архив (по
    /// записи).
    ///
    /// <para>Запись проверяется лёгким чтением — только среди типов закрытых семей и без данных:
    /// проверка стоит на КАЖДОЙ записи общих данных, и целиком читать каждую ради ответа «не вид
    /// работы» незачем. Справочника на экземпляре нет — запись не читается вовсе.</para>
    ///
    /// <para>Возврат из архива общим адресом НЕ отвергается: снять признак — всегда благо, и тем же
    /// правилом общий путь возвращает даже записи справочников модулей (<c>RecordArchiveHandlers</c>).
    /// Иначе окно выбора, вставка таблицы и «есть в архиве» при заведении, которые возвращают запись
    /// общим адресом, перестали бы возвращать вид работы. Принудительное удаление (<c>/purge</c>,
    /// CORE-34.2) тоже остаётся общим: это путь администратора, а не правка.</para>
    /// </summary>
    internal static async Task<IResult?> RefuseCommonPathAsync(
        Guid? recordId, Guid? typeId, IMediator m, IRepository<DocumentType> types, CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        foreach (var door in All)
        {
            if (CoreDirectories.Family(all, door.TypeCode) is not { } family) continue;
            var closed = typeId is { } type
                ? family.ContainsKey(type)
                : recordId is { } id
                  && (await m.Send(new CommonDataRefsByIdsQuery([.. family.Keys], [id]), ct)).Count > 0;
            if (closed)
                return Results.Conflict(new
                {
                    error = $"{door.Plural} ведутся своим адресом — {door.Door}, под правом «{door.Right}» " +
                            $"({door.Permission}). Общий адрес справочников их не заводит, не правит, не " +
                            "удаляет и не отправляет в архив.",
                    door = door.Door,
                });
        }
        return null;
    }
}
