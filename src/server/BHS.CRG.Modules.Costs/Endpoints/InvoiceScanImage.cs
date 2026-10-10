using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Читаемый образ файла счёта — что о нём знает экран (issue #1270).
/// </summary>
/// <param name="State"><see cref="InvoiceScanImage.Original" /> — образ не нужен, показывается и
/// читается сам файл; <see cref="InvoiceScanImage.Built" /> — образ построен;
/// <see cref="InvoiceScanImage.Refused" /> — построить не удалось, причина в <paramref name="Reason" />.</param>
/// <param name="Notes">Пометки построителя: чем образ отличается от файла.</param>
/// <param name="RetryHelps">Отказ не о файле, а о сервисе: тот же файл позже построится.</param>
public sealed record InvoiceImageView(
    string State, int? Pages, IReadOnlyList<string> Notes, string? Converter, DateTimeOffset? BuiltAt,
    string? Reason, bool RetryHelps);

/// <summary>
/// Читаемый образ файла счёта (эпик #1264, issue #1270): Excel и Word рядом с формой показываются
/// и распознаются по образу — PDF, который из них строит ядро.
///
/// <para><b>Образом владеет ядро, счёт о нём не знает ничего.</b> Модуль хранит только оригинал и
/// спрашивает образ портом по пути оригинала. Поэтому построение, отказ и «Перестроить» не пишут в
/// счёт и версию его не двигают: форма, открытая рядом, 409 от них не получает. Положить отметку
/// образа в строку счёта «для скорости» значило бы вернуть этот 409.</para>
///
/// <para><b>Документ — оригинал.</b> Образ — приближённая перевёрстка для чтения; скачивается,
/// запирается закрытым периодом и уходит в копию именно файл, приложенный человеком.</para>
/// </summary>
public sealed class InvoiceScanImage(IModuleRenditions renditions, IModuleBlobs blobs)
{
    public const string Original = "original";
    public const string Built = "built";
    public const string Refused = "refused";

    private static readonly InvoiceImageView AsOriginal = new(Original, null, [], null, null, null, false);

    /// <summary>
    /// Может ли файлу понадобиться образ — по записи счёта, без обращения к хранилищу.
    ///
    /// <para>⚠️ «Да» — и у файла, вид которого НЕ ОПРЕДЕЛИЛСЯ: Excel под паролем — уже не архив, а
    /// зашифрованный контейнер, и по началу файла он неотличим от чего угодно. Что это — защищённая
    /// книга, обрезанный файл или в самом деле файл другого вида, — говорит ядро, прочитав его
    /// целиком. Спросить только у «известных Excel и Word» значило бы ответить про защищённый счёт
    /// «файл другого вида» и не назвать пароль вовсе.</para>
    /// </summary>
    internal static bool Possible(Invoice invoice) =>
        invoice.ScanBlobPath is not null
        && FileKindCatalog.Find(FileKinds.Recorded(invoice.ScanMimeType)) is not { Reading: not FileReading.Rendition };

    /// <summary>
    /// Образ для экрана. Нет записи — строит: файл мог быть приложен до появления образов или
    /// приехать из резервной копии, куда образы не кладутся.
    /// </summary>
    public async Task<InvoiceImageView> ViewAsync(Invoice invoice, CancellationToken ct) =>
        Possible(invoice) ? View(await renditions.EnsureAsync(invoice.ScanBlobPath!, ct)) : AsOriginal;

    /// <summary>Построить заново — явным действием человека.</summary>
    public async Task<InvoiceImageView> RebuildAsync(Invoice invoice, CancellationToken ct)
    {
        if (!Possible(invoice))
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: перестраивать нечего — читаемый вид строится только " +
                $"для файлов {FileKindCatalog.Words(FileKindCatalog.All.Where(kind => kind.Reading == FileReading.Rendition))}.");
        var rebuilt = await renditions.RebuildAsync(invoice.ScanBlobPath!, ct);
        if (rebuilt is ModuleRendition.Refused { OtherKind: true } other)
            throw new ConflictException($"{InvoiceEndpoints.Label(invoice)}: перестраивать нечего. {other.Reason}");
        return View(rebuilt);
    }

    /// <summary>
    /// Почему файл нельзя распознать из-за образа; <c>null</c> — образ не мешает. Не строит: вопрос
    /// задаёт каждое чтение состояния распознавания. Образа ещё нет — не мешает: его построит само
    /// распознавание и причину отказа назовёт исходом. «Файл другого вида» сюда не попадает: ядро
    /// такой отказ не запоминает.
    /// </summary>
    public async Task<string?> WhyNotAsync(Invoice invoice, CancellationToken ct) =>
        Possible(invoice)
        && await renditions.FindAsync(invoice.ScanBlobPath!, ct) is ModuleRendition.Refused { OtherKind: false } refused
            ? $"файл не приведён к читаемому виду. {refused.Reason.TrimEnd('.')}"
            : null;

    /// <summary>Путь построенного образа, как он есть сейчас; <c>null</c> — образа нет.</summary>
    public async Task<string?> CurrentAsync(Invoice invoice, CancellationToken ct) =>
        invoice.ScanBlobPath is { } path && await renditions.FindAsync(path, ct) is ModuleRendition.Built built
            ? built.Path
            : null;

    /// <summary>
    /// Что отдать движку распознавания: сам файл или его образ.
    /// </summary>
    /// <returns><c>Image</c> — путь образа, если движку ушёл он; <c>null</c> — ушёл сам файл.</returns>
    public async Task<(byte[] Content, string Mime, string? Image)> ReadableAsync(
        string originalPath, CancellationToken ct)
    {
        // Состояний у ядра три, и третье — отказ: его слова и становятся исходом распознавания.
        var (path, mime, image) = await renditions.EnsureAsync(originalPath, ct) switch
        {
            ModuleRendition.AsIs asIs => (asIs.Path, asIs.Mime, (string?)null),
            ModuleRendition.Built built => (built.Path, FileKinds.Pdf, built.Path),
            var other => throw new ConflictException(
                $"Файл счёта не прочитан. {(other as ModuleRendition.Refused)?.Reason ?? "Читаемого вида у него нет."}"),
        };

        await using var stored = await blobs.OpenAsync(path, ct);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), mime, image);
    }

    /// <summary>Образ потоком; <c>null</c> — образа нет.</summary>
    public async Task<Stream?> OpenAsync(Invoice invoice, CancellationToken ct) =>
        await CurrentAsync(invoice, ct) is { } path ? await blobs.OpenAsync(path, ct) : null;

    private static InvoiceImageView View(ModuleRendition rendition) => rendition switch
    {
        ModuleRendition.Built built =>
            new(Built, built.Pages, built.Notes, built.Converter, built.BuiltAt, null, false),
        // Файл другого вида — не отказ, а «образ не положен»: экран покажет его как умеет сам.
        ModuleRendition.Refused { OtherKind: false } refused =>
            new(Refused, null, [], null, null, refused.Reason, refused.RetryHelps),
        _ => AsOriginal,
    };
}

/// <summary>Адреса читаемого образа файла счёта.</summary>
public static class InvoiceScanImageEndpoints
{
    private const string Read = "costs.invoice.read";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices").WithTags("Счета на оплату");

        group.MapGet("/{id:guid}/scan/image", GetAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapGet("/{id:guid}/scan/image/content", ContentAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapPost("/{id:guid}/scan/image", RebuildAsync)
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit));
    }

    private static async Task<Ok<InvoiceImageView>> GetAsync(
        Guid id, CostsDbContext db, InvoiceScanImage image, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        if (invoice.ScanBlobPath is null)
            throw new NotFoundException($"{InvoiceEndpoints.Label(invoice)}: файл счёта не приложен.");
        return TypedResults.Ok(await image.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Отдать образ. Потоком и под правом чтения счёта — как сам файл. Тип называет адрес, а не
    /// хранилище: образ строит ядро, и это всегда PDF.
    /// </summary>
    private static async Task<IResult> ContentAsync(
        Guid id, CostsDbContext db, InvoiceScanImage image, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        var content = await image.OpenAsync(invoice, ct)
            ?? throw new NotFoundException($"{InvoiceEndpoints.Label(invoice)}: читаемого вида у файла счёта нет.");
        return Results.Stream(content, FileKinds.Pdf);
    }

    /// <summary>
    /// «Перестроить» — построить образ заново тем конвертером, что стоит сейчас.
    ///
    /// <para>⚠️ Версию счёта (<c>If-Match</c>) адрес не спрашивает: в счёт он не пишет — образ лежит
    /// у ядра. По той же причине его не останавливает и запертый счёт: документ закрытого периода —
    /// оригинал, а он остаётся тем же.</para>
    /// </summary>
    private static async Task<Ok<InvoiceImageView>> RebuildAsync(
        Guid id, CostsDbContext db, InvoiceScanImage image, CancellationToken ct) =>
        TypedResults.Ok(await image.RebuildAsync(await InvoiceEndpoints.FindAsync(db, id, ct), ct));
}
