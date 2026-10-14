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
    ///
    /// <para>Предел числа общих представлений таблицы восстановление не соблюдает: копия — не
    /// действие человека, и отказ на двести первом представлении оставил бы её восстановленной
    /// наполовину. Служба проверяет предел только на входе в группу, так что принесённое сверх него
    /// остаётся правимым.</para>
    /// </summary>
    private async Task RestoreTableViewsAsync(
        BackupTableView[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        // Запись проверяется тем же правилом, что на адресе сохранения, и по ширине колонок: копия
        // приходит от другой сборки или правленой руками, и негодная строка обязана стать названным
        // пропуском, а не исключением, которое откатит восстановление целиком (ревью PR #1323).
        var parsed = items.Select(i => (Item: i, Title: SavedTableViewRules.Title(i.Title), State: Checked(i.State))).ToList();
        var (ok, rejected) = Split(parsed, p =>
            p.Title.Problem is null && p.State.Problem is null
            && !string.IsNullOrWhiteSpace(p.Item.TableAddress) && p.Item.TableAddress.Length <= SavedTableView.AddressMax
            && (p.Item.BasedOn is null || p.Item.BasedOn.Length <= SavedTableView.AddressMax));
        Warn(warnings, rejected.Count, "общих представлений таблиц", "название или настройку эта версия не принимает");

        var existing = await db.SavedTableViews.ToListAsync(ct);
        var byId = existing.ToDictionary(v => v.Id);

        // Представление, которое автор здесь вернул в свои, копия общим не делает — это его решение.
        var (incoming, personal) = Split(ok, p => !(byId.TryGetValue(p.Item.Id, out var row) && !row.Shared));
        Warn(warnings, personal.Count, "общих представлений таблиц", "здесь автор вернул их в свои");

        // Занятые имена — БЕЗ строк, которые эта же копия переименует: иначе цепочка «A→B, B→C»
        // упиралась бы в имя, которое к концу восстановления свободно, и первое представление
        // оставалось бы прежним с ложным «уже есть». Дубль ключа здесь возможен (индекс по lower()
        // базы и приведение регистра сервером — не одно и то же правило), поэтому TryAdd.
        var carried = incoming.Select(p => p.Item.Id).ToHashSet();
        var taken = new Dictionary<(string, string), Guid>();
        foreach (var row in existing.Where(v => v.Shared && !carried.Contains(v.Id)))
            taken.TryAdd(NameKey(row.TableAddress, row.Title), row.Id);

        var accepted = new List<(BackupTableView Item, string Title, string State)>();
        var named = 0;
        // Лежащие здесь, которым копия нового имени не дала: они остаются под прежним.
        var staying = new List<SavedTableView>();
        foreach (var (item, (title, _), (state, _)) in incoming)
        {
            if (!taken.TryAdd(NameKey(item.TableAddress, title), item.Id))
            {
                named++;
                if (byId.TryGetValue(item.Id, out var stays)) staying.Add(stays);
                continue;
            }
            accepted.Add((item, title, SavedTableViewRules.Store(state!)));
        }

        // Оставшееся под прежним именем это имя ЗАНИМАЕТ — а выше оно считалось свободным. Кому копия
        // успела его отдать, тот уступает и остаётся при своём; список растёт, пока уступать некому.
        for (var i = 0; i < staying.Count; i++)
        {
            var key = NameKey(staying[i].TableAddress, staying[i].Title);
            if (taken.TryGetValue(key, out var holder) && holder != staying[i].Id
                && accepted.FindIndex(a => a.Item.Id == holder) is >= 0 and var at)
            {
                accepted.RemoveAt(at);
                named++;
                if (byId.TryGetValue(holder, out var back)) staying.Add(back);
            }
            taken[key] = staying[i].Id;
        }
        Warn(warnings, named, "общих представлений таблиц", "общее представление с таким названием у таблицы уже есть");

        // Лежащее здесь и не отличающееся от копии не трогаем: иначе повторное восстановление той же
        // копии сдвигало бы версию каждой строки, и открытые формы получали бы «тем временем изменили»
        // без единого изменения.
        var changing = accepted.Where(a => byId.TryGetValue(a.Item.Id, out var row) && Differs(row, a)).ToList();

        // Имена меняются в два шага. Уникальный индекс проверяется построчно, а порядок строк в
        // сохранении не наш: обмен именами или та же цепочка «A→B, B→C» упёрлись бы в него посреди
        // сохранения. Сначала переименуемые получают имя, которого нет ни у кого, потом — своё.
        foreach (var (item, title, _) in changing)
        {
            var row = byId[item.Id];
            if (NameKey(row.TableAddress, row.Title) != NameKey(item.TableAddress, title))
                row.Carry(row.TableAddress, row.BasedOn, $"~{row.Id:N}", row.State, row.UpdatedAt);
        }
        await db.SaveChangesAsync(ct);

        var created = 0;
        foreach (var (item, title, state) in accepted)
        {
            if (byId.TryGetValue(item.Id, out var row))
            {
                if (changing.Any(c => c.Item.Id == item.Id))
                    row.Carry(item.TableAddress, item.BasedOn, title, state, item.UpdatedAt);
                continue;
            }

            db.SavedTableViews.Add(SavedTableView.Restore(
                item.Id, item.TableAddress, item.BasedOn, title, state, item.CreatedAt, item.UpdatedAt));
            created++;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Общие представления таблиц", created, changing.Count);

        static (string, string) NameKey(string table, string title) => (table, title.ToLowerInvariant());

        // Состояния сравниваются разобранными: в базе JSON лежит в записи самой базы, а не в нашей.
        static bool Differs(SavedTableView row, (BackupTableView Item, string Title, string State) a) =>
            row.Title != a.Title || row.TableAddress != a.Item.TableAddress || row.BasedOn != a.Item.BasedOn
            || Checked(row.State) is not { State: { } here } || SavedTableViewRules.Store(here) != a.State;

        static (TableViewState? State, string? Problem) Checked(string? stored)
        {
            if (stored is null) return (null, "настройки нет");
            try { return SavedTableViewRules.Normalize(SavedTableViewRules.Read(stored)); }
            catch (System.Text.Json.JsonException)
            {
                return (null, "настройка не разбирается");
            }
        }
    }
}
