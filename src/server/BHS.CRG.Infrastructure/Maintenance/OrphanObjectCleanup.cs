using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Осиротевший объект — сколько и где.</summary>
/// <param name="Sets">На несуществующих комплектах.</param>
/// <param name="Sections">На несуществующих разделах.</param>
/// <param name="Constructions">На несуществующих стройках.</param>
/// <param name="WithData">Из них с непустыми данными — их потеря заметна, в отличие от пустых профилей.</param>
public record OrphanCleanupReport(int Sets, int Sections, int Constructions, int WithData)
{
    public int Total => Sets + Sections + Constructions;
}

/// <summary>
/// Уборка объектов, чьё место расположения больше не существует (issue #739).
///
/// <para>Как они появлялись: у <c>domain_objects</c> нет внешнего ключа на комплект — ось
/// расположения полиморфна, — и база уносила разделы за стройкой и комплекты за разделом, а объекты
/// оставляла. Прикладной каскад был только у комплекта. Причина закрыта там же, где заведена эта
/// уборка, но след остаётся: на рабочей базе такие записи уже были, а восстановление старой
/// резервной копии способно привезти их снова — поэтому инструмент, а не разовый SQL.</para>
///
/// <para>Действие администратора с предварительным подсчётом (<c>dryRun</c>), как и прочее
/// обслуживание: удаление тут окончательное, и увидеть числа до него важнее, чем сэкономить шаг.
/// <c>WithData</c> в отчёте отделяет пустые объекты-профили уровня (их создают лениво при открытии
/// экрана, и терять там нечего) от записей с содержимым — если такие есть, это повод посмотреть
/// глазами, а не жать «удалить».</para>
///
/// <para>Идемпотентна: повторный прогон на убранной базе находит ноль.</para>
/// </summary>
public class OrphanObjectCleanup(AppDbContext db)
{
    /// <param name="dryRun">Только посчитать, ничего не удаляя.</param>
    public async Task<OrphanCleanupReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        // Отбор в базе: сравнение с тремя таблицами дешевле любого обхода в памяти, а в этой же
        // таблице лежат многомегабайтные JSONB документов — тянуть их сюда незачем.
        var orphans = await db.DomainObjects
            .Where(o => o.ScopeId != null
                && ((o.ScopeLevel == CatalogScope.Set
                     && !db.DocumentSets.Any(s => s.Id == o.ScopeId!.Value))
                 || (o.ScopeLevel == CatalogScope.Section
                     && !db.Sections.Any(s => s.Id == o.ScopeId!.Value))
                 || (o.ScopeLevel == CatalogScope.Construction
                     && !db.Constructions.Any(c => c.Id == o.ScopeId!.Value))))
            .ToListAsync(ct);

        var report = new OrphanCleanupReport(
            Sets: orphans.Count(o => o.ScopeLevel == CatalogScope.Set),
            Sections: orphans.Count(o => o.ScopeLevel == CatalogScope.Section),
            Constructions: orphans.Count(o => o.ScopeLevel == CatalogScope.Construction),
            WithData: orphans.Count(o => o.Data.RootElement.EnumerateObject().Any()));

        if (!dryRun && orphans.Count > 0)
        {
            db.DomainObjects.RemoveRange(orphans); // фасета + generated_files каскадируются в БД
            await db.SaveChangesAsync(ct);
        }

        return report;
    }
}
