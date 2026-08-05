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
/// <para><b>Порядок операций и чем платим.</b> Сначала хранилище, потом запись в реестр. Сбой между
/// ними оставляет объект, который лежит, но не читается: отказ закрытый, данные целы, лечится
/// повторной загрузкой. Обратный порядок дал бы запись о несуществующем объекте — отказ открытый,
/// и хуже: проверка перестала бы означать то, что означает.</para>
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
        await RegisterAsync(path, fileName, contentType, ct);
        return path;
    }

    public async Task PutAsync(
        string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        await inner.PutAsync(blobPath, content, contentType, ct);
        // Сюда приходит восстановление из бэкапа: путь задан манифестом, объект возвращается на своё
        // прежнее место. Без этой строки восстановленная система не отдавала бы ни одного файла.
        await RegisterAsync(blobPath, fileName: null, contentType, ct);
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

    public async Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        await inner.DeleteAsync(blobPath, ct);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BlobRegistry.Where(e => e.Path == blobPath).ExecuteDeleteAsync(ct);
    }

    /// <summary>Значится ли путь за приложением. Единственный вопрос, ради которого заведён реестр.</summary>
    private async Task<bool> IsKnownAsync(string blobPath, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobRegistry.AsNoTracking().AnyAsync(e => e.Path == blobPath, ct);
    }

    private async Task RegisterAsync(string path, string? fileName, string? mimeType, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Повтор — не ошибка: PutAsync восстановления кладёт объект на прежний путь, а разовый
            // сбор мог его уже записать. Проверяем до вставки, чтобы не ловить нарушение уникальности
            // как исключение там, где это штатный ход.
            if (await db.BlobRegistry.AnyAsync(e => e.Path == path, ct)) return;

            db.BlobRegistry.Add(BlobRegistryEntry.Create(path, fileName, mimeType));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Объект уже в хранилище. Уронить здесь загрузку значило бы поменять сохранённый файл на
            // ошибку у пользователя; отказ и так закрытый — файл не прочитается, пока запись не
            // появится. Поэтому пишем в лог и отдаём путь наверх.
            log.LogError(ex, "Объект сохранён, но не попал в реестр ({Path}) — файл не будет отдаваться", path);
        }
    }
}
