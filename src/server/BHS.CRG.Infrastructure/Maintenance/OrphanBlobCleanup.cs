using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Осиротевшие объекты хранилища — сколько, сколько весят и сколько ещё рано трогать.</summary>
/// <param name="Registered">Всего объектов числится за приложением.</param>
/// <param name="Referenced">Из них на что-то ссылается база.</param>
/// <param name="Orphans">Ни на что не ссылается и достаточно старые — эти и уберутся.</param>
/// <param name="TooYoung">Ни на что не ссылается, но моложе порога — пропускаем (см. класс).</param>
/// <param name="Bytes">Суммарный вес удаляемых; недоступные в размер не входят.</param>
/// <param name="Missing">Из удаляемых нет в самом хранилище — уйдёт только запись реестра.</param>
/// <param name="Sample">Несколько имён — посмотреть глазами перед необратимым действием.</param>
/// <param name="Deleted">Сколько удалено на самом деле; при подсчёте — ноль.</param>
/// <param name="MinAgeHours">Возрастной порог, с которым считали.</param>
public record OrphanBlobReport(
    int Registered, int Referenced, int Orphans, int TooYoung,
    long Bytes, int Missing, IReadOnlyList<string> Sample, int Deleted, int MinAgeHours);

/// <summary>
/// Сборщик объектов хранилища, на которые больше никто не ссылается (issue #741).
///
/// <para><b>Что чинит.</b> Удаление документа, комплекта, раздела или стройки убирало строки
/// <c>generated_files</c> и сам документ качества, но объекты в хранилище не трогало. Диалог при
/// этом обещает «и их сгенерированные PDF» — обещание выполнялось наполовину: из интерфейса файл
/// исчезал, из бакета не уходил никогда.</para>
///
/// <para><b>Почему сборщик, а не удаление в каждой точке.</b> Точек записи в хранилище четырнадцать
/// (перечислены в <see cref="Storage.RegisteredBlobStorage" />), точек удаления столько же, и
/// чинить по одной значит закрыть один выход из многих — ошибка, которая в этом проекте повторялась
/// шесть раз подряд. Здесь выход один: что бы и как бы ни удалили, объект без ссылок будет найден
/// и убран. Мест удаления при этом можно вовсе не знать.</para>
///
/// <para><b>Возрастной порог — не перестраховка.</b> Вложение попадает в хранилище раньше, чем путь
/// на него попадает в реквизиты: клиент грузит файл, получает путь и лишь потом сохраняет форму.
/// Между этими двумя моментами объект ни на что не ссылается и от сироты неотличим. Порог в сутки
/// делает эту щель неразличимо узкой; без него сборщик отбирал бы у человека файл, который тот
/// прямо сейчас прикрепляет.</para>
///
/// <para><b>Чего сборщик НЕ видит.</b> Объекты бакета, которых нет в реестре. Реестр — определение
/// «наш объект» (issue #672), и всё, что приложение записало после его появления, там есть; всё,
/// что было до, занёс разовый сбор по живым данным. Не покрыт единственный случай: объект,
/// осиротевший ДО появления реестра, — сбор искал по держателям, а держателя у него уже не было.
/// Такие видны только перебором бакета, а перебор с удалением «всего, чего мы не знаем» — операция
/// принципиально иного веса, и заводить её заодно неправильно.</para>
///
/// <para>Действие администратора с предварительным подсчётом (<c>dryRun</c>), как уборка потерянных
/// объектов рядом: удаление окончательное, файл восстановить неоткуда.</para>
/// </summary>
public class OrphanBlobCleanup(
    AppDbContext db,
    LiveBlobPathScan scan,
    IBlobStorage blobs,
    ILogger<OrphanBlobCleanup> log)
{
    /// <summary>Сутки — с запасом больше любого разумного «загрузил и не сохранил».</summary>
    public const int DefaultMinAgeHours = 24;

    /// <summary>Сколько имён показать в отчёте — ровно чтобы узнать «а, это то самое».</summary>
    private const int SampleSize = 10;

    /// <param name="dryRun">Только посчитать, ничего не удаляя.</param>
    /// <param name="minAgeHours">Возрастной порог; по умолчанию <see cref="DefaultMinAgeHours" />.</param>
    public async Task<OrphanBlobReport> RunAsync(
        bool dryRun, int? minAgeHours = null, CancellationToken ct = default)
    {
        var ageHours = Math.Max(0, minAgeHours ?? DefaultMinAgeHours);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-ageHours);

        var live = await scan.RunAsync(ct);

        // Реестр тянем проекцией: путь, дата и имя — всё, что нужно, а строк там столько же, сколько
        // объектов в бакете.
        var registered = await db.BlobRegistry.AsNoTracking()
            .Select(e => new { e.Path, e.CreatedAt, e.FileName })
            .ToListAsync(ct);

        var unreferenced = registered.Where(e => !live.Contains(e.Path)).ToList();
        var young = unreferenced.Count(e => e.CreatedAt > cutoff);
        var doomed = unreferenced.Where(e => e.CreatedAt <= cutoff).ToList();

        // Размер спрашиваем у хранилища — в реестре его нет. Отсутствующий объект даёт null: это
        // не сбой, а запись, пережившая свой файл (ручная уборка бакета, восстановление копии), и
        // такую запись уборка тоже обязана снять.
        long bytes = 0;
        var missing = 0;
        foreach (var entry in doomed)
        {
            var size = await blobs.GetSizeAsync(entry.Path, ct);
            if (size is { } value) bytes += value; else missing++;
        }

        var deleted = 0;
        if (!dryRun)
        {
            foreach (var entry in doomed)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Через то же хранилище, что и вся остальная работа: оно убирает объект и снимает
                    // запись реестра одним действием. Обойди мы его — реестр обещал бы удалённое.
                    await blobs.DeleteAsync(entry.Path, ct);
                    deleted++;
                }
                catch (Exception ex)
                {
                    // Один недоступный объект не должен обрывать уборку: остальные удалятся, а
                    // повторный прогон подберёт этот. Молчать нельзя — иначе отчёт покажет успех
                    // там, где половина осталась.
                    log.LogWarning(ex, "Не удалось удалить осиротевший объект хранилища ({Path})", entry.Path);
                }
            }

            if (deleted > 0)
                log.LogInformation("Уборка хранилища: удалено объектов {Deleted}, освобождено байт {Bytes}",
                    deleted, bytes);
        }

        return new OrphanBlobReport(
            Registered: registered.Count,
            Referenced: registered.Count - unreferenced.Count,
            Orphans: doomed.Count,
            TooYoung: young,
            Bytes: bytes,
            Missing: missing,
            Sample: doomed.Take(SampleSize).Select(e => e.FileName ?? e.Path).ToList(),
            Deleted: deleted,
            MinAgeHours: ageHours);
    }
}
