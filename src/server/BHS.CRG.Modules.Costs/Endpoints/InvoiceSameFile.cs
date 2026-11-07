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
/// <param name="SupplierName">Название поставщика. <c>null</c> при <paramref name="SupplierId" /> — названия нет:
/// справочник ядра не ответил либо запись поставщика удалена. Это «не знаем», а не «без поставщика»; что
/// именно из двух — говорит форма самого счёта, окну повтора это не нужно.</param>
/// <param name="SupplierArchived">Поставщик в архиве. <c>null</c> — не знаем: названия нет по той же причине,
/// что и у <paramref name="SupplierName" />. Не <c>false</c>: «не в архиве» — утверждение, которого справочник
/// не делал. Без поставщика — <c>false</c>: в архиве нечему быть.</param>
/// <param name="State">Состояние документа словами сервера, как в реестре (<c>InvoiceListItem</c>).</param>
/// <param name="Payment">Оплата словами сервера, как в реестре.</param>
/// <param name="CreatedByName">Имя заведшего на момент заведения; <c>null</c> — счёт заведён до снимка
/// имени, и строки «кто завёл» нет.</param>
/// <param name="Mine">Завёл тот, кто спрашивает, — по учётной записи, а не по имени: тёзки бывают, а у
/// прежних счетов имени нет вовсе.</param>
public sealed record InvoiceSameFile(
    Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total, string? ScanFileName, DateTimeOffset CreatedAt,
    Guid? SupplierId, string? SupplierName, bool? SupplierArchived, string State, string Payment,
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

    /// <summary>
    /// Двойники счёта по его файлу — найденные, но ещё не названные: названия поставщиков чтение счёта
    /// берёт тем же вызовом справочника, что и названия своих архивных сторон (<see cref="SameFileTwins.Suppliers" />),
    /// — один вызов на ответ, а не два.
    /// </summary>
    internal static async Task<SameFileTwins> FindAsync(CostsDbContext db, Invoice invoice, CancellationToken ct) =>
        invoice.TrustedFingerprint is { } print
            ? await TrustedAsync(db, [print], except: invoice.Id, ct)
            : SameFileTwins.None;

    /// <summary>
    /// Двойники каждого отпечатка — одним запросом по индексу отпечатка, сколько бы их ни спросили
    /// (COST-6.7). Это единственный поиск «того же файла»: чтение счёта (<see cref="FindAsync" />) идёт
    /// тем же путём с одним отпечатком, и правило доверия печати с пределом живут в одном месте.
    /// </summary>
    /// <param name="prints">Отпечатки в нижнем регистре, без повторов.</param>
    /// <param name="except">Счёт, который себе не двойник.</param>
    internal static async Task<SameFileTwins> TrustedAsync(
        CostsDbContext db, IReadOnlyCollection<string> prints, Guid? except, CancellationToken ct)
    {
        var self = except ?? Guid.Empty;
        var rows = await RowsAsync(db, prints, self, Margin, ct);

        // Запас выбран до конца, а доверенных меньше предела — старшие строки отпечатка недоверенные
        // (путь файла сменился в обход приёма). Такой отпечаток добирается целиком, без предела: иначе
        // ответ «двойников нет» прятал бы доверенных за недоверенными, и бросок не предупредил бы о
        // повторе. Случай редкий — вторым запросом, а не запасом побольше у каждого.
        var starved = rows.GroupBy(r => r.ScanFingerprint!)
            .Where(g => g.Count() == Margin && g.Count(IsTrusted) < Limit)
            .Select(g => g.Key).ToList();
        if (starved.Count > 0)
            rows = [.. rows.Where(r => !starved.Contains(r.ScanFingerprint!)),
                .. await RowsAsync(db, starved, self, int.MaxValue, ct)];

        return new SameFileTwins(rows.Where(IsTrusted)
            .GroupBy(r => r.ScanFingerprint!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Row>)[.. g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(Limit)]));
    }

    /// <summary>Запас сверх предела — на строки, которым верить нельзя: их отсеивает сверка уже в памяти.</summary>
    private const int Margin = Limit * 2;

    private static bool IsTrusted(Row r) => Invoice.Trusted(r.ScanBlobPath, r.ScanFingerprint, r.ScanFingerprintSeal) is not null;

    /// <summary>
    /// Один запрос: отпечатки, которые у счетов есть (IN по индексу), и к каждому — его счета боковым
    /// подзапросом с пределом. Предел — на КАЖДЫЙ отпечаток, а не на ответ: бланк, загруженный сотню раз,
    /// не вправе вытеснить остальные файлы пакета. Проекцией, а не сущностями: счёт несёт реквизиты
    /// целиком, а нужны поля двойника.
    /// <para>⚠️ <c>GroupBy(…).SelectMany(g => g.Take(n))</c> короче, но EF его не переводит — отказ при запросе.</para>
    /// </summary>
    private static Task<List<Row>> RowsAsync(
        CostsDbContext db, IReadOnlyCollection<string> prints, Guid self, int take, CancellationToken ct) =>
        db.Invoices.AsNoTracking()
            .Where(i => i.ScanFingerprint != null && prints.Contains(i.ScanFingerprint))
            .Select(i => i.ScanFingerprint)
            .Distinct()
            .SelectMany(print => db.Invoices
                .Where(i => i.ScanFingerprint == print && i.Id != self)
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Take(take))
            .Select(i => new Row(
                i.Id, i.Number, i.IssuedOn, i.Total, i.ScanFileName, i.CreatedAt, i.ScanBlobPath,
                i.ScanFingerprint, i.ScanFingerprintSeal, i.SupplierId, i.State, i.Payment, i.CreatedBy,
                i.CreatedByName))
            .ToListAsync(ct);

    /// <summary>Строка двойника, как её читает поиск, — до сверки печати и до названия поставщика.</summary>
    internal sealed record Row(
        Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total, string? ScanFileName, DateTimeOffset CreatedAt,
        string? ScanBlobPath, string? ScanFingerprint, string? ScanFingerprintSeal, Guid? SupplierId,
        InvoiceState State, InvoicePaymentState Payment, Guid? CreatedBy, string? CreatedByName);

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
        var found = await TrustedAsync(db, prints, except: null, ct);
        // Названия поставщиков — одним вопросом на ответ и только когда спрашивать есть о ком: без
        // двойников вопрос к справочнику ядра не ходит вовсе.
        var names = found.Suppliers.Count == 0
            ? null
            : await catalog.RefsAsync(CostsDirectories.Organization, found.Suppliers, ct);
        return TypedResults.Ok<IReadOnlyList<SameFileAnswer>>(
            [.. prints.Select(p => new SameFileAnswer(p, found.Of(p, names, user.Id)))]);
    }

    /// <summary>
    /// Отпечатки вопроса — в нижний регистр и без повторов, в порядке вопроса. Отказ называет, что не так:
    /// клиент, получивший безымянный 400, не отличил бы свою ошибку от поломки сервера.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string?[]? fingerprint)
    {
        if (fingerprint is not { Length: > 0 })
            throw new InvalidRequestException(
                "Не назван ни один отпечаток файла: спросите параметром «fingerprint=…» хотя бы один.");
        // По номеру, а не FirstOrDefault: тот не отличил бы «плохого нет» от «плохой — пустой элемент».
        var at = Array.FindIndex(fingerprint, p => !IsFingerprint(p));
        if (at >= 0 && (fingerprint[at] ?? "") is var bad)
            throw new InvalidRequestException(
                $"«{(bad.Length > FingerprintLength ? bad[..FingerprintLength] + "…" : bad)}» — не отпечаток файла: " +
                $"нужны {FingerprintLength} шестнадцатеричных знаков (SHA-256 содержимого).");

        // Предел — по РАЗНЫМ отпечаткам, после схлопывания повторов: пакет, где файл выбран дважды,
        // спрашивает о нём один раз, и отказ за повторы был бы отказом ни за что. Длину самого адреса
        // ограничивают прокси и сервер.
        List<string> prints = [.. fingerprint.Select(p => p!.ToLowerInvariant()).Distinct()];
        if (prints.Count > MaxFingerprints)
            throw new InvalidRequestException(
                $"Отпечатков {prints.Count}, а за один вопрос принимается не больше {MaxFingerprints} — " +
                "столько, сколько файлов в пакете загрузки. Разделите вопрос на части.");

        return prints;
    }

    private static bool IsFingerprint(string? print) =>
        print is { Length: FingerprintLength } && print.All(Uri.IsHexDigit);
}

/// <summary>
/// Двойники, найденные по отпечаткам и сверенные с печатью, — но ещё не названные. Поставщиков называет
/// тот, кто зовёт справочник: вопрос до выгрузки — сам, чтение счёта — вместе со своими сторонами.
/// </summary>
internal sealed class SameFileTwins(IReadOnlyDictionary<string, IReadOnlyList<InvoiceSameFiles.Row>> twins)
{
    public static readonly SameFileTwins None = new(new Dictionary<string, IReadOnlyList<InvoiceSameFiles.Row>>());

    /// <summary>Поставщики всех двойников, без повторов. Пусто — справочник спрашивать не о ком.</summary>
    public IReadOnlyList<Guid> Suppliers { get; } =
        [.. twins.Values.SelectMany(t => t).Select(r => r.SupplierId).OfType<Guid>().Distinct()];

    /// <summary>Есть ли у отпечатка доверенные двойники.</summary>
    public bool Any(string print) => twins.ContainsKey(print);

    /// <summary>Двойники отпечатка, от старшего к младшему; у отпечатка без двойников — пусто.</summary>
    /// <param name="names">Ответ справочника на <see cref="Suppliers" /> (может нести и другие записи);
    /// <c>null</c> — справочник не прочитан.</param>
    /// <param name="reader">Кто спрашивает — для <see cref="InvoiceSameFile.Mine" />.</param>
    public IReadOnlyList<InvoiceSameFile> Of(string? print, IReadOnlyList<ModuleCatalogRef>? names, Guid? reader)
    {
        if (print is null || !twins.TryGetValue(print, out var rows)) return [];
        // Порт уникальности записей в ответе не обещает: повтор идентификатора — не повод ронять чтение счёта.
        var known = names?.DistinctBy(n => n.Id).ToDictionary(n => n.Id);
        return [.. rows.Select(r =>
        {
            var supplier = r.SupplierId is { } id ? known?.GetValueOrDefault(id) : null;
            return new InvoiceSameFile(r.Id, r.Number, r.IssuedOn, r.Total, r.ScanFileName, r.CreatedAt,
                r.SupplierId, supplier?.DisplayName, r.SupplierId is null ? false : supplier?.Archived,
                InvoiceRequisites.Label(r.State), InvoiceRequisites.Label(r.Payment),
                r.CreatedByName, reader is { } me && r.CreatedBy == me);
        })];
    }
}
