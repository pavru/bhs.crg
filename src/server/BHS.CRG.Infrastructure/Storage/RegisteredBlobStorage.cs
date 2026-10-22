using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Storage;

/// <summary>
/// Хранилище, которое знает, какие объекты создало оно само (issue #672, п. 2-3).
///
/// Обёртка вокруг настоящего хранилища, а не правка в эндпоинте выдачи, — и это главное решение
/// здесь. Проверку требовалось поставить в двух местах: на выдаче вложения (п. 2) и на материализации
/// картинки при генерации (п. 3, произвольный <c>blobPath</c> из реквизитов попадал в PDF и в
/// отладочный ZIP в обход выдачи). Две проверки в двух слоях разъезжаются — это ровно тот случай,
/// когда чинят один выход из четырёх. Здесь выход один: <b>всё</b> чтение хранилища идёт через
/// <see cref="DownloadAsync" />, и обе двери закрываются одним замком.
///
/// По той же причине наполнение реестра живёт здесь: точек записи в хранилище четырнадцать
/// (вложения, ассеты шаблонов, генерация, сборка комплекта, файлы наборов, разрезание PDF,
/// восстановление из бэкапа, разовые миграции). Перечислять их в проверке — значит завести список,
/// который разойдётся с кодом при первой же новой точке. Запись проходит через этот класс вся, без
/// исключений, поэтому реестр полон по построению, а не по внимательности.
///
/// <para><b>Порядок операций и чем платим.</b> Сначала хранилище, потом запись в реестр. Обратный
/// порядок дал бы запись о несуществующем объекте — отказ открытый, и хуже: проверка перестала бы
/// означать то, что означает.</para>
///
/// <para>Сбой записи в реестр <b>роняет операцию целиком</b>, а не проглатывается. Соблазн был
/// обратный — объект ведь уже сохранён, зачем расстраивать пользователя. Но проглоченный сбой даёт
/// 200 и путь, который клиент запишет в реквизиты, а файл по нему не откроется никогда; хуже того,
/// экспорт копии тихо пропускает недоступные блобы (<c>BackupService</c> ловит отказ выдачи и лишь
/// пишет предупреждение), так что одна секундная ошибка БД превращается в постоянно битое вложение
/// и в копию, в которой его нет. Громкий отказ сейчас честнее: остаётся осиротевший объект в
/// хранилище — плата заметно меньшая.</para>
///
/// <para><b>Владелец</b> (ТЗ CORE-37.2) сверяется тем же замком: путь должен не просто значиться в
/// реестре, а значиться за тем, кто спрашивает. Методы без владельца спрашивают от имени ядра — так
/// общий адрес вложений, генерация и прочие службы ядра не видят файлов модулей, ничего о них не
/// зная. Файл чужого владельца — тот же ответ, что незнакомый путь: «файла нет», без разницы, по
/// которой одно отличалось бы от другого.</para>
///
/// <para><b>Время жизни.</b> Хранилище — синглтон, <c>AppDbContext</c> — scoped, поэтому контекст
/// берётся через <see cref="IServiceScopeFactory" /> на каждую операцию. Своего состояния у класса
/// нет.</para>
/// </summary>
public class RegisteredBlobStorage(
    IBlobStorage inner,
    IServiceScopeFactory scopes,
    ILogger<RegisteredBlobStorage> log) : IBlobStorage
{
    public Task<string> UploadAsync(
        string fileName, Stream content, string contentType, CancellationToken ct = default)
        => UploadAsync(fileName, content, contentType, BlobOwner.Core, ct);

    public async Task<string> UploadAsync(
        string fileName, Stream content, string contentType, string owner, CancellationToken ct = default)
    {
        var path = await inner.UploadAsync(fileName, content, contentType, ct);
        await RegisterAsync(path, owner, fileName, contentType, restoring: false);
        return path;
    }

    public Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
        => PutAsync(blobPath, content, contentType, BlobOwner.Core, ct);

    public async Task PutAsync(
        string blobPath, Stream content, string contentType, string owner, CancellationToken ct = default)
    {
        await inner.PutAsync(blobPath, content, contentType, ct);
        // Сюда приходит восстановление из бэкапа: путь задан манифестом, объект возвращается на своё
        // прежнее место. Без этой строки восстановленная система не отдавала бы ни одного файла.
        await RegisterAsync(blobPath, owner, fileName: null, contentType, restoring: true);
    }

    public Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
        => DownloadAsync(blobPath, BlobOwner.Core, ct);

    public async Task<Stream> DownloadAsync(string blobPath, string owner, CancellationToken ct = default)
    {
        if (!await OwnsAsync(blobPath, owner, ct))
        {
            // Путь пишем только в лог. Клиенту — обычное «не найдено»: разница между «объекта нет»
            // и «объект есть, но не наш» сама по себе сведения, по которым бакет и перебирают. Так
            // же — «объект есть, но другого владельца»: файл модуля общим адресом неотличим от
            // отсутствующего (ТЗ CORE-37.2).
            log.LogWarning(
                "Отказ в выдаче объекта хранилища: путь не значится в реестре за владельцем {Owner} ({Path})",
                owner, blobPath);
            throw new NotFoundException("Файл не найден.");
        }

        return await inner.DownloadAsync(blobPath, ct);
    }

    /// <summary>
    /// Размер объекта — через тот же замок, что и выдача (issue #711). Незарегистрированный путь
    /// размера не имеет: иначе появился бы способ узнать о чужом объекте по разнице ответов, а
    /// «всё чтение хранилища идёт через одну дверь» перестало бы быть правдой.
    /// </summary>
    public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
        => GetSizeAsync(blobPath, BlobOwner.Core, ct);

    public async Task<long?> GetSizeAsync(string blobPath, string owner, CancellationToken ct = default)
        => await OwnsAsync(blobPath, owner, ct) ? await inner.GetSizeAsync(blobPath, ct) : null;

    /// <summary>
    /// Отсутствующие пути — с тем же замком, что у <see cref="GetSizeAsync" />, но реестр спрашивается
    /// ОДИН раз за все пути (issue #1439). Незарегистрированный путь для нас «нет» — хранилище о нём не
    /// спрашивают вовсе; спрашивают только о зарегистрированных.
    ///
    /// <para>Ради этого и заведено: у копии, снятой при недоступном хранилище, на новой установке
    /// путей вне архива тысячи, и ни один из них не зарегистрирован — прежде это были тысячи пар
    /// «запрос к реестру + запрос к хранилищу», теперь один запрос к базе.</para>
    /// </summary>
    public Task<IReadOnlySet<string>> FindAbsentAsync(
        IEnumerable<string> blobPaths, CancellationToken ct = default)
        => FindAbsentAsync(blobPaths, BlobOwner.Core, ct);

    public async Task<IReadOnlySet<string>> FindAbsentAsync(
        IEnumerable<string> blobPaths, string owner, CancellationToken ct = default)
    {
        var asked = blobPaths.ToHashSet(StringComparer.Ordinal);
        if (asked.Count == 0) return asked;

        HashSet<string> known;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var list = asked.ToList();
            known = (await Owned(db, owner)
                    .Where(e => list.Contains(e.Path)).Select(e => e.Path).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }

        asked.ExceptWith(known);
        foreach (var path in known)
            if (await inner.GetSizeAsync(path, ct) is null) asked.Add(path);
        return asked;
    }

    public Task DeleteAsync(string blobPath, CancellationToken ct = default)
        => DeleteAsync(blobPath, BlobOwner.Core, ct);

    /// <summary>
    /// Удалить файл этого владельца. Файл другого владельца остаётся на месте — молча для
    /// вызывающего, с записью в журнал: пути в реквизитах пишет клиент, и удаление документа, в который
    /// вписан путь скана счёта, иначе удалило бы скан (ТЗ CORE-37.2). Путь вне реестра удаляется, как
    /// прежде: он ничей, и беречь его не для кого.
    /// </summary>
    public async Task DeleteAsync(string blobPath, string owner, CancellationToken ct = default)
    {
        if (owner != BlobOwner.Any && await OwnerOfAsync(blobPath, ct) is { } holder && holder != owner)
        {
            log.LogWarning(
                "Удаление объекта хранилища от имени {Owner} отклонено: его владелец {Holder} ({Path})",
                owner, holder, blobPath);
            return;
        }

        await RemoveAsync(blobPath, ct);
    }

    /// <summary>Удалить объект, его запись в реестре и его читаемый образ — без вопроса о владельце.</summary>
    private async Task RemoveAsync(string blobPath, CancellationToken ct)
    {
        await inner.DeleteAsync(blobPath, ct);

        // Токена снова нет, и по той же причине, что при записи: объекта уже нет, а отмена оставила
        // бы реестр обещающим то, чего не существует.
        List<string> images;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobRegistry.Where(e => e.Path == blobPath).ExecuteDeleteAsync();

            // Читаемый образ живёт, пока жив оригинал (issue #1269), и привязка эта — здесь, в
            // одном месте: удаление файла идёт через этот класс всё, поэтому владельцу оригинала
            // помнить об образе не нужно — и забыть нельзя.
            //
            // Сначала запись, потом сам образ. Упавшее между ними даёт образ без держателя — его
            // найдёт уборка осиротевших. Обратный порядок дал бы запись, которая держит образ
            // оригинала, которого уже нет: такую не убрал бы никто.
            //
            // Одним запросом, а не «прочитать и удалить»: удаление идёт у каждого файла хранилища,
            // а образ есть у единиц; и между чтением и удалением путь образа мог бы смениться.
            images = await db.Database
                .SqlQuery<string>($"""
                    DELETE FROM renditions WHERE "OriginalBlobPath" = {blobPath}
                    RETURNING COALESCE("ImageBlobPath", '') AS "Value"
                    """)
                .ToListAsync();
        }

        foreach (var image in images.Where(path => path.Length > 0))
        {
            // Оригинала уже нет, и это — успех вызова: владелец по нему снимает ссылку у своей
            // записи. Отказ здесь наверх не уходит и отмене не подчиняется: иначе у владельца
            // остался бы путь файла, который больше не откроется. Не удалённый образ остался без
            // держателя — его заберёт уборка осиротевших.
            try
            {
                // Тем же путём, что и любой файл: образ — такой же объект с записью в реестре.
                // Своего образа у него нет, так что глубже одного шага это не уходит. Владелец
                // образа — владелец оригинала, а право удалить оригинал уже проверено.
                await RemoveAsync(image, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Образ удалённого файла не удалён, его заберёт уборка осиротевших ({Path})", image);
            }
        }
    }

    /// <summary>
    /// Значится ли путь за приложением — и за этим владельцем. Единственный вопрос, ради которого
    /// заведён реестр.
    /// </summary>
    private async Task<bool> OwnsAsync(string blobPath, string owner, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await Owned(db, owner).AnyAsync(e => e.Path == blobPath, ct);
    }

    private async Task<string?> OwnerOfAsync(string blobPath, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobRegistry.AsNoTracking()
            .Where(e => e.Path == blobPath).Select(e => e.Owner).FirstOrDefaultAsync(ct);
    }

    /// <summary>Записи этого владельца; <see cref="BlobOwner.Any" /> — все (резервная копия).</summary>
    private static IQueryable<BlobRegistryEntry> Owned(AppDbContext db, string owner)
    {
        var entries = db.BlobRegistry.AsNoTracking();
        return owner == BlobOwner.Any ? entries : entries.Where(e => e.Owner == owner);
    }

    /// <param name="restoring">Восстановление копии: запись, уцелевшая на этом пути, получает владельца
    /// из копии. Обычная запись путь не повторяет (он новый), и владельца не меняет никто.</param>
    private async Task RegisterAsync(string path, string owner, string? fileName, string? mimeType, bool restoring)
    {
        // «Любой» — вопрос копии, а не владелец: записать его значило бы открыть файл всем дверям. Отказ
        // ДО записи в реестр, но уже после хранилища: объект ляжет сиротой, его заберёт уборка.
        if (owner == BlobOwner.Any)
            throw new ArgumentException("Файл кладётся от имени владельца, а не «любого».", nameof(owner));

        // Токена запроса здесь СОЗНАТЕЛЬНО нет. Объект к этому моменту уже в хранилище, и отмена
        // (клиент отключился сразу после того, как большая загрузка дошла) сорвала бы ровно ту
        // запись, ради которой выбран порядок «сначала хранилище, потом реестр»: файл лёг бы
        // навсегда нечитаемым. Отмене здесь подчиняться нечему — работа уже сделана наполовину.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Повтор — не ошибка: PutAsync восстановления кладёт объект на прежний путь, а он мог
            // быть записан раньше. Проверяем до вставки, чтобы не ловить нарушение уникальности как
            // исключение там, где это штатный ход.
            if (await db.BlobRegistry.FirstOrDefaultAsync(e => e.Path == path) is { } known)
            {
                if (restoring && known.Owner != owner)
                {
                    known.RestoreOwner(owner);
                    await db.SaveChangesAsync();
                }
                return;
            }

            db.BlobRegistry.Add(BlobRegistryEntry.Create(path, owner, fileName, mimeType));
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolation(ex))
        {
            // Проверка выше и вставка не атомарны: две одновременные записи одного пути (повтор
            // запроса, импорт копии с дублями) обе видят «пути нет». Проигравший получает нарушение
            // уникальности, но путь в реестре ЕСТЬ — это успех, а не сбой. Без этой ветки в журнал
            // уходило бы тревожное и попросту неверное «файл не будет отдаваться».
            log.LogDebug(ex, "Путь уже зарегистрирован параллельной записью ({Path})", path);
        }
    }
}
