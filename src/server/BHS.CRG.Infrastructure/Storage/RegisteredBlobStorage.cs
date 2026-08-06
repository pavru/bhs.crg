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
/// <para><b>Время жизни.</b> Хранилище — синглтон, <c>AppDbContext</c> — scoped, поэтому контекст
/// берётся через <see cref="IServiceScopeFactory" /> на каждую операцию. Своего состояния у класса
/// нет.</para>
/// </summary>
public class RegisteredBlobStorage(
    IBlobStorage inner,
    IServiceScopeFactory scopes,
    ILogger<RegisteredBlobStorage> log) : IBlobStorage
{
    public async Task<string> UploadAsync(
        string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = await inner.UploadAsync(fileName, content, contentType, ct);
        await RegisterAsync(path, fileName, contentType);
        return path;
    }

    public async Task PutAsync(
        string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        await inner.PutAsync(blobPath, content, contentType, ct);
        // Сюда приходит восстановление из бэкапа: путь задан манифестом, объект возвращается на своё
        // прежнее место. Без этой строки восстановленная система не отдавала бы ни одного файла.
        await RegisterAsync(blobPath, fileName: null, contentType);
    }

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        if (!await IsKnownAsync(blobPath, ct))
        {
            // Путь пишем только в лог. Клиенту — обычное «не найдено»: разница между «объекта нет»
            // и «объект есть, но не наш» сама по себе сведения, по которым бакет и перебирают.
            log.LogWarning("Отказ в выдаче объекта хранилища: путь не значится в реестре ({Path})", blobPath);
            throw new NotFoundException("Файл не найден.");
        }

        return await inner.DownloadAsync(blobPath, ct);
    }

    /// <summary>
    /// Размер объекта — через тот же замок, что и выдача (issue #711). Незарегистрированный путь
    /// размера не имеет: иначе появился бы способ узнать о чужом объекте по разнице ответов, а
    /// «всё чтение хранилища идёт через одну дверь» перестало бы быть правдой.
    /// </summary>
    public async Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default)
        => await IsKnownAsync(blobPath, ct) ? await inner.GetSizeAsync(blobPath, ct) : null;

    public async Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        await inner.DeleteAsync(blobPath, ct);

        // Токена снова нет, и по той же причине, что при записи: объекта уже нет, а отмена оставила
        // бы реестр обещающим то, чего не существует.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BlobRegistry.Where(e => e.Path == blobPath).ExecuteDeleteAsync();
    }

    /// <summary>Значится ли путь за приложением. Единственный вопрос, ради которого заведён реестр.</summary>
    private async Task<bool> IsKnownAsync(string blobPath, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobRegistry.AsNoTracking().AnyAsync(e => e.Path == blobPath, ct);
    }

    private async Task RegisterAsync(string path, string? fileName, string? mimeType)
    {
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
            if (await db.BlobRegistry.AnyAsync(e => e.Path == path)) return;

            db.BlobRegistry.Add(BlobRegistryEntry.Create(path, fileName, mimeType));
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicatePath(ex))
        {
            // Проверка выше и вставка не атомарны: две одновременные записи одного пути (повтор
            // запроса, импорт копии с дублями) обе видят «пути нет». Проигравший получает нарушение
            // уникальности, но путь в реестре ЕСТЬ — это успех, а не сбой. Без этой ветки в журнал
            // уходило бы тревожное и попросту неверное «файл не будет отдаваться».
            log.LogDebug(ex, "Путь уже зарегистрирован параллельной записью ({Path})", path);
        }
    }

    /// <summary>Нарушение уникальности пути (код 23505 у Postgres) — против любого поставщика по коду.</summary>
    private static bool IsDuplicatePath(DbUpdateException ex)
        => ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505";
}
