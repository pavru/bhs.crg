using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Documents;

/// <summary>
/// Заведение записи справочника для порта модулей (см. <see cref="ICatalogIntake" />, issue #1077).
///
/// <para><b>Замок — совещательный и один на все заведения.</b> Ограничения единственности в базе у
/// записи справочника нет и быть не может: поле единственности — ключ внутри JSON, а у записи бывает
/// основа, от которой оно наследуется. Поэтому «такой ещё нет» и создание идут под замком до конца
/// транзакции: второе нажатие ждёт первое и видит его запись. Один ключ на все типы — операция
/// редкая, а долю секунды ожидания человек не заметит.</para>
///
/// <para>⚠️ Замок держит только ЭТА дверь. Форма каталога его не берёт: организацию, заведённую там в
/// ту же секунду, он не увидит. Действующих дублей ядро не запрещает и без гонки.</para>
///
/// <para>Создаёт запись та же команда, что и у двери «Общих данных»: охрана записи стоит в ней, и
/// поле, запертое в схеме, отсюда тоже не заполнить.</para>
/// </summary>
public sealed class CatalogIntakeService(
    AppDbContext db, ISender mediator, IDomainObjectRepository objects, IRepository<DocumentType> types)
    : ICatalogIntake
{
    public async Task<CatalogIntakeOutcome> CreateAsync(CatalogIntakeRequest request, CancellationToken ct = default)
    {
        var all = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);

        // Раскладка — до замка: схема от него не зависит, а отказ по схеме замка не стоит.
        var (data, refusals) = CatalogIntakeLayout.Build(request, all);
        if (data is null) return new(null, [], refusals);

        using (data)
        {
            // Вне транзакции замок снялся бы тем же запросом. Чужую транзакцию не закрываем.
            await using var own = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKeys.CatalogIntake})", ct);

            // Ищем среди типа И его подтипов, хотя заводим только сам тип: подтип организации —
            // та же организация. Показ, а не выбор: архивная с этим значением — тоже «уже есть».
            var family = all.Keys
                .Where(id => DocumentTypeSchemaReader.IsSameOrDescendant(id, request.TypeId, all))
                .ToList();
            var same = (await objects.FieldValuesAsync(family, request.UniqueField, RecordsFor.Display, ct))
                .Where(v => !v.Unreadable && v.Value?.Trim() == request.UniqueValue)
                .Select(v => v.Record)
                .ToList();
            if (same.Count > 0) return new(null, same, []);

            try
            {
                var created = await mediator.Send(new CreateCommonDataEntryCommand(
                    request.Name, request.TypeId, data, CatalogScope.System, null), ct);
                if (own is not null) await own.CommitAsync(ct);
                return new(created, [], []);
            }
            catch (ArchivedTwinException twin)
            {
                // Совпадение по ключу идентичности, а не по нашему полю: значение у архивной другое
                // или не прочитано. Заводить вторую молча нельзя — решает человек, в каталоге.
                return new(null,
                    [new CommonDataRef(twin.ArchivedId, request.TypeId, twin.ArchivedName, Archived: true)], []);
            }
        }
    }
}
