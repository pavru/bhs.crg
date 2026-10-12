using System.Security.Cryptography;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Счёт, к которому приложен файл с тем же содержимым. Назван не как дубликат по реквизитам: у
/// черновика из файла нет ни номера, ни даты, и узнают его по времени заведения и имени файла.
/// </summary>
public sealed record InvoiceSameFile(
    Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total, string? ScanFileName, DateTimeOffset CreatedAt);

/// <summary>
/// «Тот же файл приложен к другому счёту» (issue #1295) — оговорка, а не запрет, как у дубликата по
/// реквизитам (ТЗ COST-6.2): в одном файле бывает несколько счетов.
///
/// <para><b>Отпечаток считает модуль</b>, при приёме, по содержимому ПРИСЛАННОГО файла. Хранилище его
/// не даёт: реестр блобов знает путь, имя и тип, а ETag — свойство поставщика хранилища, а не хеш
/// содержимого. Читаемый образ офисного файла здесь ни при чём: он строится позже и лежит у ядра.</para>
///
/// <para><b>Оговорка вычисляется при чтении счёта</b>, а не запоминается при загрузке: так она видна у
/// обоих счетов, переживает перезагрузку страницы и гаснет сама, когда один из пары удалён или файл у
/// него заменён.</para>
///
/// <para>⚠️ У счёта, заведённого до появления отпечатка, его нет — и с такими счетами файл не
/// сравнивается вовсе. Это «неизвестно», а не «совпадений нет»: экран нигде не утверждает, что файл
/// новый.</para>
/// </summary>
internal static class InvoiceSameFiles
{
    /// <summary>
    /// SHA-256 содержимого, шестнадцатеричной строкой в нижнем регистре. Своим проходом по файлу — как
    /// определение вида: поток, уже прочитанный до конца, дал бы отпечаток пустоты, одинаковый у всех.
    /// </summary>
    internal static async Task<string> FingerprintAsync(IFormFile file, CancellationToken ct)
    {
        await using var content = file.OpenReadStream();
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
    }

    /// <summary>Другие счета с тем же содержимым файла, от старшего к младшему.</summary>
    internal static async Task<IReadOnlyList<InvoiceSameFile>> FindAsync(
        CostsDbContext db, Invoice invoice, CancellationToken ct)
    {
        if (invoice.ScanFingerprint is not { } print) return [];

        var found = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Id != invoice.Id && i.ScanFingerprint == print)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        return [.. found.Select(i => new InvoiceSameFile(i.Id, i.Number, i.IssuedOn, i.Total, i.ScanFileName, i.CreatedAt))];
    }
}
