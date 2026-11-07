using System.Security.Cryptography;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Счёт, к которому приложен файл с тем же содержимым. Назван не как дубликат по реквизитам: у
/// черновика из файла нет ни номера, ни даты, и узнают его по времени заведения и имени файла.
///
/// <para>Одна запись на окно повтора, форму и пакет (COST-6.7): окну нужно узнать счёт — чей он, в каком
/// состоянии и кто его завёл, — а не только номер и время.</para>
/// </summary>
/// <param name="SupplierName">Название поставщика. <c>null</c> при <paramref name="SupplierId" /> — справочник
/// ядра не ответил: «не знаем», а не «без поставщика».</param>
/// <param name="State">Состояние документа словами сервера, как в реестре (<c>InvoiceListItem</c>).</param>
/// <param name="Payment">Оплата словами сервера, как в реестре.</param>
/// <param name="CreatedByName">Имя заведшего на момент заведения; <c>null</c> — счёт заведён до снимка
/// имени, и строки «кто завёл» нет.</param>
/// <param name="Mine">Завёл тот, кто спрашивает, — по учётной записи, а не по имени: тёзки бывают, а у
/// прежних счетов имени нет вовсе.</param>
public sealed record InvoiceSameFile(
    Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total, string? ScanFileName, DateTimeOffset CreatedAt,
    Guid? SupplierId, string? SupplierName, bool SupplierArchived, string State, string Payment,
    string? CreatedByName, bool Mine);

/// <summary>Ответ вопроса по отпечаткам — запись на каждый спрошенный отпечаток, в порядке вопроса.</summary>
/// <param name="Twins">Счета с этим файлом. Пусто — двойников среди счетов с доверенным отпечатком нет;
/// это НЕ «файл новый»: счета без отпечатка не сравниваются вовсе.</param>
public sealed record SameFileAnswer(string Fingerprint, IReadOnlyList<InvoiceSameFile> Twins);

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
/// новый. То же — у счёта, чей путь файла сменился в обход приёма (восстановление из копии старше
/// отпечатка): отпечаток у него от другого файла, и верить ему нельзя
/// (<see cref="Invoice.TrustedFingerprint" />).</para>
///
/// <para><b>Третий проход по файлу — нарочно</b> (ревью PR #1305). Считать отпечаток по ходу выгрузки
/// значило бы доверить его тому, как хранилище читает поток: повтор части, перемотка — и отпечаток
/// неверен без единого отказа. Проход по файлу, уже лежащему на диске сервера, стоит доли секунды
/// рядом с выгрузкой и распознаванием.</para>
/// </summary>
public static class InvoiceSameFiles
{
    /// <summary>
    /// Сколько двойников называет оговорка. Предел есть потому, что один бланк могут загрузить десятки
    /// раз, а поиск идёт на каждом чтении счёта; форме хватает первых — остальные она не различит.
    /// </summary>
    public const int Limit = 10;

    /// <summary>
    /// SHA-256 содержимого, шестнадцатеричной строкой в нижнем регистре. Своим проходом по файлу — как
    /// определение вида: поток, уже прочитанный до конца, дал бы отпечаток пустоты, одинаковый у всех.
    /// </summary>
    public static async Task<string> FingerprintAsync(IFormFile file, CancellationToken ct)
    {
        await using var content = file.OpenReadStream();
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
    }

    /// <summary>
    /// Сколько отпечатков принимает вопрос — столько, сколько файлов в пакете загрузки (<c>MAX_FILES</c>
    /// клиента, <c>features/costs/scanBatch.ts</c>). Адрес с пятьюдесятью отпечатками — около 3,8 КБ и
    /// укладывается в предел строки запроса nginx (8 КБ по умолчанию).
    /// </summary>
    public const int MaxFingerprints = 50;

    /// <summary>Длина отпечатка: SHA-256 шестнадцатеричной строкой.</summary>
    public const int FingerprintLength = 64;

    /// <summary>Другие счета с тем же содержимым файла, от старшего к младшему.</summary>
    internal static async Task<IReadOnlyList<InvoiceSameFile>> FindAsync(
        CostsDbContext db, IModuleCatalog catalog, Guid? reader, Invoice invoice, CancellationToken ct)
    {
        if (invoice.TrustedFingerprint is not { } print) return [];
        return (await ByFingerprintsAsync(db, catalog, reader, [print], except: invoice.Id, ct))[print];
    }

    /// <summary>
    /// Двойники каждого отпечатка — одним запросом по индексу отпечатка, сколько бы их ни спросили
    /// (COST-6.7). Это единственный поиск «того же файла»: чтение счёта (<see cref="FindAsync" />) зовёт
    /// его с одним отпечатком, и правило доверия печати с пределом живут в одном месте.
    /// </summary>
    /// <param name="prints">Отпечатки в нижнем регистре, без повторов.</param>
    /// <param name="except">Счёт, который себе не двойник.</param>
    /// <returns>Запись на каждый отпечаток из <paramref name="prints" />, в том числе пустую.</returns>
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<InvoiceSameFile>>> ByFingerprintsAsync(
        CostsDbContext db, IModuleCatalog catalog, Guid? reader, IReadOnlyCollection<string> prints, Guid? except,
        CancellationToken ct)
    {
        var self = except ?? Guid.Empty;

        // Один запрос: отпечатки, которые у счетов есть (IN по индексу), и к каждому — его счета боковым
        // подзапросом с пределом. Предел — на КАЖДЫЙ отпечаток, а не на ответ: бланк, загруженный сотню
        // раз, не вправе вытеснить остальные файлы пакета. Запас сверх предела — на строки, которым верить
        // нельзя: их отсеивает сверка ниже, уже в памяти. Проекцией, а не сущностями: счёт несёт реквизиты
        // целиком, а нужны поля двойника.
        // ⚠️ GroupBy(…).SelectMany(g => g.Take(n)) короче, но EF его не переводит — отказ при запросе.
        var found = await db.Invoices.AsNoTracking()
            .Where(i => i.ScanFingerprint != null && prints.Contains(i.ScanFingerprint))
            .Select(i => i.ScanFingerprint)
            .Distinct()
            .SelectMany(print => db.Invoices
                .Where(i => i.ScanFingerprint == print && i.Id != self)
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Take(Limit * 2))
            .Select(i => new
            {
                i.Id, i.Number, i.IssuedOn, i.Total, i.ScanFileName, i.CreatedAt, i.ScanBlobPath,
                i.ScanFingerprint, i.ScanFingerprintSeal, i.SupplierId, i.State, i.Payment, i.CreatedBy,
                i.CreatedByName,
            })
            .ToListAsync(ct);

        var trusted = found
            .Where(i => Invoice.Trusted(i.ScanBlobPath, i.ScanFingerprint, i.ScanFingerprintSeal) is not null)
            .GroupBy(i => i.ScanFingerprint!)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).Take(Limit).ToList());

        // Названия поставщиков — одним вопросом на ответ и только когда спрашивать есть о ком: без
        // двойников чтение счёта к справочнику ядра не ходит вовсе.
        var suppliers = trusted.Values.SelectMany(t => t).Select(i => i.SupplierId).OfType<Guid>().Distinct().ToList();
        var names = suppliers.Count == 0
            ? null
            : (await catalog.RefsAsync(CostsDirectories.Organization, suppliers, ct))?.ToDictionary(r => r.Id);

        return prints.ToDictionary(p => p, p => (IReadOnlyList<InvoiceSameFile>)(trusted.TryGetValue(p, out var twins)
            ? [.. twins.Select(i =>
            {
                var supplier = i.SupplierId is { } id ? names?.GetValueOrDefault(id) : null;
                return new InvoiceSameFile(i.Id, i.Number, i.IssuedOn, i.Total, i.ScanFileName, i.CreatedAt,
                    i.SupplierId, supplier?.DisplayName, supplier?.Archived == true,
                    InvoiceRequisites.Label(i.State), InvoiceRequisites.Label(i.Payment),
                    i.CreatedByName, reader is { } r && i.CreatedBy == r);
            })]
            : []));
    }

    /// <summary>
    /// Вопрос «нет ли уже счёта с этим файлом» — ДО выгрузки (COST-6.7, лист «Ф» 27.10.2026): браузер
    /// считает отпечатки брошенных файлов и спрашивает один раз на бросок. Только читает.
    ///
    /// <para><c>GET</c>, а не <c>POST</c> с телом: это чтение, а <c>POST</c> в модуле — запись, и перепись
    /// <c>If-Match</c> спрашивала бы о нём.</para>
    /// </summary>
    internal static async Task<Ok<IReadOnlyList<SameFileAnswer>>> AskAsync(
        [FromQuery(Name = "fingerprint")] string[]? fingerprint, CostsDbContext db, IModuleCatalog catalog,
        IModuleUser user, CancellationToken ct)
    {
        var prints = Parse(fingerprint);
        var found = await ByFingerprintsAsync(db, catalog, user.Id, prints, except: null, ct);
        return TypedResults.Ok<IReadOnlyList<SameFileAnswer>>([.. prints.Select(p => new SameFileAnswer(p, found[p]))]);
    }

    /// <summary>
    /// Отпечатки вопроса — в нижний регистр и без повторов, в порядке вопроса. Отказ называет, что не так:
    /// клиент, получивший безымянный 400, не отличил бы свою ошибку от поломки сервера.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string[]? fingerprint)
    {
        if (fingerprint is not { Length: > 0 })
            throw new InvalidRequestException(
                "Не назван ни один отпечаток файла: спросите параметром «fingerprint=…» хотя бы один.");
        if (fingerprint.Length > MaxFingerprints)
            throw new InvalidRequestException(
                $"Отпечатков {fingerprint.Length}, а за один вопрос принимается не больше {MaxFingerprints} — " +
                "столько, сколько файлов в пакете загрузки. Разделите вопрос на части.");

        var bad = fingerprint.FirstOrDefault(p => !IsFingerprint(p));
        if (bad is not null)
            throw new InvalidRequestException(
                $"«{(bad.Length > FingerprintLength ? bad[..FingerprintLength] + "…" : bad)}» — не отпечаток файла: " +
                $"нужны {FingerprintLength} шестнадцатеричных знаков (SHA-256 содержимого).");

        return [.. fingerprint.Select(p => p.ToLowerInvariant()).Distinct()];
    }

    private static bool IsFingerprint(string? print) =>
        print is { Length: FingerprintLength } && print.All(Uri.IsHexDigit);
}
