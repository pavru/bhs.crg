using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Осиротевшие объекты хранилища — сколько, сколько весят и сколько ещё рано трогать.</summary>
/// <param name="Registered">Всего объектов числится за приложением.</param>
/// <param name="Referenced">Из них на что-то ссылается база.</param>
/// <param name="Orphans">Ни на что не ссылается и достаточно старые — кандидаты на уборку.</param>
/// <param name="TooYoung">Ни на что не ссылается, но моложе порога — пропускаем (см. класс).</param>
/// <param name="Batch">Сколько из них берёт ЭТОТ прогон: не больше <see cref="OrphanBlobCleanup.MaxPerRun" />.</param>
/// <param name="Bytes">При подсчёте — вес партии; при уборке — сколько освобождено НА САМОМ ДЕЛЕ.</param>
/// <param name="Missing">Из партии нет в самом хранилище — уйдёт только запись реестра.</param>
/// <param name="Sample">Несколько имён — посмотреть глазами перед необратимым действием.</param>
/// <param name="Deleted">Сколько удалено; при подсчёте — ноль.</param>
/// <param name="Failed">Сколько не удалось удалить — эти остаются до следующего прогона.</param>
/// <param name="Remaining">Сколько кандидатов останется после этого прогона.</param>
/// <param name="StorageUnreachable">Хранилище не отвечает — числа недостоверны, уборка не делалась.</param>
/// <param name="MinAgeHours">Возрастной порог, с которым считали.</param>
public record OrphanBlobReport(
    int Registered, int Referenced, int Orphans, int TooYoung, int Batch,
    long Bytes, int Missing, IReadOnlyList<string> Sample,
    int Deleted, int Failed, int Remaining, bool StorageUnreachable, int MinAgeHours);

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

    /// <summary>
    /// Потолок объектов на один прогон.
    ///
    /// <para>Уборка идёт прямо в запросе, и на объект приходится четыре обращения (размер, удаление
    /// и по запросу к базе на каждое). Первый прогон на системе, копившей файлы годами, — самый
    /// большой из всех, и без потолка он пережил бы таймаут прокси: соединение обрывается,
    /// пользователь видит отказ, а тысячи объектов к тому моменту уже удалены. С потолком прогон
    /// заведомо укладывается в минуту, а остаток честно показан в отчёте — кнопку жмут ещё раз.</para>
    /// </summary>
    public const int MaxPerRun = 2000;

    /// <summary>Сколько имён показать в отчёте — ровно чтобы узнать «а, это то самое».</summary>
    private const int SampleSize = 10;

    /// <summary>Сколько живых объектов пробуем, проверяя доступность хранилища.</summary>
    private const int ProbeSize = 3;

    /// <param name="dryRun">Только посчитать, ничего не удаляя.</param>
    /// <param name="minAgeHours">
    /// Возрастной порог. Наружу (в эндпоинт) НЕ выведен сознательно: это единственная защита
    /// прикрепляемого прямо сейчас файла, и знать её значение снаружи некому. Параметр существует
    /// ради тестов и разбора инцидентов.
    /// </param>
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
        var batch = doomed.Take(MaxPerRun).ToList();

        OrphanBlobReport Report(long bytes, int missing, int deleted, int failed, bool unreachable) =>
            new(Registered: registered.Count,
                Referenced: registered.Count - unreferenced.Count,
                Orphans: doomed.Count,
                TooYoung: young,
                Batch: batch.Count,
                Bytes: bytes,
                Missing: missing,
                Sample: batch.Take(SampleSize).Select(e => e.FileName ?? e.Path).ToList(),
                Deleted: deleted,
                Failed: failed,
                Remaining: doomed.Count - deleted,
                StorageUnreachable: unreachable,
                MinAgeHours: ageHours);

        // Размер спрашиваем у хранилища — в реестре его нет. Отсутствующий объект даёт null: это
        // не сбой, а запись, пережившая свой файл (ручная уборка бакета, восстановление копии), и
        // такую запись уборка тоже обязана снять.
        long size = 0;
        var missing = 0;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in batch)
        {
            var value = await blobs.GetSizeAsync(entry.Path, ct);
            if (value is { } bytes) { sizes[entry.Path] = bytes; size += bytes; } else missing++;
        }

        // «Размера нет» и «хранилище не отвечает» с виду одно и то же: MinIOBlobStorage возвращает
        // null на ЛЮБОЙ отказ. Разница решающая — при недоступном хранилище отчёт скажет «все
        // объекты и так пропали, уйдут только записи», администратор подтвердит, и записи реестра
        // уйдут у живых файлов. А без записи в реестре файл не отдаётся и больше никогда не
        // попадётся этой же уборке: он станет невидимым навсегда.
        //
        // Различаем пробой по ЗАВЕДОМО живому объекту: на него ссылается база, значит он должен
        // быть на месте. Молчат все пробы — молчит хранилище, а не файлы.
        if (missing > 0 && !await StorageAnswersAsync(registered.Where(e => live.Contains(e.Path)).Select(e => e.Path), ct))
        {
            log.LogWarning("Уборка хранилища отменена: хранилище не отвечает (не удалось получить размер "
                + "ни одного из проверенных объектов, на которые ссылается база)");
            return Report(bytes: 0, missing: missing, deleted: 0, failed: 0, unreachable: true);
        }

        if (dryRun) return Report(size, missing, deleted: 0, failed: 0, unreachable: false);

        long freed = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var entry in batch)
        {
            // На отмене выходим, а не бросаем: отвечать уже некому, но отчёт и запись в журнал
            // должны сказать, сколько успели, — иначе прерванный прогон выглядит как несделанный.
            if (ct.IsCancellationRequested) break;
            try
            {
                // Через то же хранилище, что и вся остальная работа: оно убирает объект и снимает
                // запись реестра одним действием. Обойди мы его — реестр обещал бы удалённое.
                await blobs.DeleteAsync(entry.Path, ct);
                deleted++;
                // Байты считаем ТОЛЬКО за удалённое. Складывать их заранее было ошибкой: отказ
                // хранилища на каждом объекте давал «удалено 0, освобождено 1,2 ГБ».
                freed += sizes.GetValueOrDefault(entry.Path);
            }
            catch (Exception ex)
            {
                // Один недоступный объект не должен обрывать уборку: остальные удалятся, а
                // повторный прогон подберёт этот. Молчать нельзя — иначе отчёт покажет успех
                // там, где половина осталась.
                failed++;
                log.LogWarning(ex, "Не удалось удалить осиротевший объект хранилища ({Path})", entry.Path);
            }
        }

        log.LogInformation("Уборка хранилища: удалено объектов {Deleted}, освобождено байт {Bytes}, "
            + "не удалось {Failed}, осталось кандидатов {Remaining}",
            deleted, freed, failed, doomed.Count - deleted);

        return Report(freed, missing, deleted, failed, unreachable: false);
    }

    /// <summary>
    /// Отвечает ли хранилище: спрашиваем размер у нескольких объектов, на которые ссылается база.
    /// Проб несколько, потому что и живая ссылка бывает битой; ответ хотя бы одной означает, что
    /// хранилище на месте, а пропавшие — действительно пропавшие. Проверять нечем (живых объектов
    /// нет вовсе) — считаем, что доступно: иначе первая же уборка на пустой системе встала бы.
    /// </summary>
    private async Task<bool> StorageAnswersAsync(IEnumerable<string> livePaths, CancellationToken ct)
    {
        var probed = 0;
        foreach (var path in livePaths.Take(ProbeSize))
        {
            probed++;
            if (await blobs.GetSizeAsync(path, ct) is not null) return true;
        }
        return probed == 0;
    }
}
