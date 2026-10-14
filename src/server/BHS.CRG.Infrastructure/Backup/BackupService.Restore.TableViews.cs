using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Tables;
using BHS.CRG.Infrastructure.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    /// <summary>
    /// Общие представления таблиц из копии (ТЗ CORE-33, issue #1095).
    ///
    /// <para>Только общие: личное адресует учётную запись, а учётные записи копия не переносит. Автор
    /// по той же причине не едет — представление приходит без автора, и правит его держатель права на
    /// общие представления.</para>
    ///
    /// <para>Upsert по идентификатору: им представление названо в адресе экрана, и закладки обязаны
    /// открываться после переноса. Таблицу копия не проверяет — её может не быть в этой сборке (модуль
    /// не поставлен): строка ложится и ждёт, список представлений такой таблицы никому не отдаётся.</para>
    /// </summary>
    private async Task RestoreTableViewsAsync(
        BackupTableView[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        // Состояние проверяется тем же правилом, что на адресе сохранения: копия приходит от другой
        // сборки, и негодное дерево отбора отказало бы уже при открытии — всем читателям разом.
        var parsed = items.Select(i => (Item: i, Title: SavedTableViewRules.Title(i.Title), State: Checked(i.State))).ToList();
        var (ok, rejected) = Split(parsed, p =>
            p.Title.Problem is null && p.State.Problem is null
            && !string.IsNullOrWhiteSpace(p.Item.TableAddress) && p.Item.TableAddress.Length <= SavedTableView.AddressMax);
        Warn(warnings, rejected.Count, "общих представлений таблиц", "название или настройку эта версия не принимает");

        var existing = await db.SavedTableViews.ToListAsync(ct);
        var byId = existing.ToDictionary(v => v.Id);
        // Имя общего уникально на таблицу — занято и тем, что уже лежит здесь, и тем, что легло из
        // этой же копии строкой выше.
        var taken = existing.Where(v => v.Shared)
            .ToDictionary(v => (v.TableAddress, v.Title.ToLowerInvariant()), v => v.Id);

        int created = 0, updated = 0, personal = 0, named = 0;
        foreach (var (item, (title, _), (state, _)) in ok)
        {
            byId.TryGetValue(item.Id, out var row);

            // Автор успел вернуть представление в свои — это его решение здесь, и копия его не отменяет.
            if (row is { Shared: false }) { personal++; continue; }

            var key = (item.TableAddress, title.ToLowerInvariant());
            if (taken.TryGetValue(key, out var holder) && holder != item.Id) { named++; continue; }
            if (row is not null) taken.Remove((row.TableAddress, row.Title.ToLowerInvariant()));
            taken[key] = item.Id;

            var stored = SavedTableViewRules.Store(state!);
            if (row is not null) { row.Change(title, true, stored); updated++; }
            else
            {
                db.SavedTableViews.Add(SavedTableView.Restore(
                    item.Id, item.TableAddress, item.BasedOn, title, stored, item.CreatedAt, item.UpdatedAt));
                created++;
            }
        }

        Warn(warnings, personal, "общих представлений таблиц", "здесь автор вернул их в свои");
        Warn(warnings, named, "общих представлений таблиц", "общее представление с таким названием у таблицы уже есть");

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Общие представления таблиц", created, updated);

        static (TableViewState? State, string? Problem) Checked(string stored)
        {
            try { return SavedTableViewRules.Normalize(SavedTableViewRules.Read(stored)); }
            catch (System.Text.Json.JsonException)
            {
                return (null, "настройка не разбирается");
            }
        }
    }
}
