using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Читаемый образ файла счёта — что о нём знает экран (issue #1270).
/// </summary>
/// <param name="State"><see cref="InvoiceScanImage.Original" /> — образ не нужен, показывается и
/// читается сам файл; <see cref="InvoiceScanImage.Built" /> — образ построен;
/// <see cref="InvoiceScanImage.Refused" /> — построить не удалось, причина в <paramref name="Reason" />.</param>
/// <param name="Notes">Пометки построителя: чем образ отличается от файла.</param>
/// <param name="AboutFile">Отказ — свойство файла, и он запомнен: сам собой вид не построится.
/// Иначе дело в сервисе или установке (конвертер занят, молчит, не настроен), и следующий вопрос
/// строит заново.</param>
/// <param name="Layout">Откуда раскладка вида на бумагу (issue #1456): <see cref="InvoiceScanImage.ByProfile" />
/// — книга без своей раскладки, профиль печати к ней применяется (или применится перестроением);
/// <see cref="InvoiceScanImage.ByFile" /> — раскладку задал автор книги; <c>null</c> — файл не книга
/// <c>.xlsx</c> или вида нет: профили к нему не относятся.</param>
/// <param name="Profile">Профиль, по которому построен вид; <c>null</c> — не записан или удалён.</param>
/// <param name="ProfileState"><c>current</c>, <c>changed</c> (правлен после построения), <c>deleted</c>,
/// <c>none</c> (вид построен до профилей); <c>null</c> — профиль к виду не относится или подставить его не
/// удалось.</param>
/// <param name="LayoutNote">Пометка построителя о раскладке по профилю печати (issue #1495) — отдельно от
/// <paramref name="Notes" />, в которых её нет: экран показывает её у выбора профиля, а не строкой.
/// <c>null</c> — вид по профилю не раскладывался.</param>
public sealed record InvoiceImageView(
    string State, int? Pages, IReadOnlyList<string> Notes, string? Converter, DateTimeOffset? BuiltAt,
    string? Reason, bool AboutFile,
    string? Layout = null, InvoiceImageProfile? Profile = null, string? ProfileState = null,
    string? LayoutNote = null);

/// <summary>Профиль печати вида — именем, как он называется сейчас.</summary>
public sealed record InvoiceImageProfile(Guid Id, string Name);

/// <summary>«Перестроить» с выбором (issue #1456). Тело необязательно: без него — профиль прежнего вида.</summary>
/// <param name="PrintProfileId">Профиль печати, выбранный человеком; несуществующий — 404.</param>
public sealed record InvoiceImageRebuild(Guid? PrintProfileId);

/// <summary>
/// Распознать нельзя, потому что у файла нет читаемого образа. Своим типом — чтобы исход
/// распознавания назвал это своей причиной (<see cref="InvoiceScanImage.NoImage" />), а не общей
/// «прочитанное не записалось»: читать здесь никто и не начинал.
/// </summary>
public sealed class InvoiceImageRefusedException(string message) : ConflictException(message);

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
public sealed class InvoiceScanImage(
    [FromKeyedServices(CostsModule.ModuleCode)] IModuleRenditions renditions,
    [FromKeyedServices(CostsModule.ModuleCode)] IModuleBlobs blobs)
{
    public const string Original = "original";
    public const string Built = "built";
    public const string Refused = "refused";

    /// <summary>Раскладка по профилю печати — <see cref="InvoiceImageView.Layout" />.</summary>
    public const string ByProfile = "profile";

    /// <summary>Раскладку задал автор книги — <see cref="InvoiceImageView.Layout" />.</summary>
    public const string ByFile = "file";

    /// <summary>Вид причины отказа распознавания: образа у файла нет.</summary>
    public const string NoImage = "NoImage";

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
        Possible(invoice) ? View(await renditions.EnsureAsync(invoice.ScanBlobPath!, ct), invoice) : AsOriginal;

    /// <summary>
    /// Построить заново — явным действием человека. <paramref name="profileId" /> — профиль печати,
    /// выбранный им (issue #1456); не назван — ядро возьмёт профиль прежнего вида. Выбор помнит ядро, за
    /// файлом, а не счёт: в счёт «Перестроить» не пишет.
    /// </summary>
    public async Task<InvoiceImageView> RebuildAsync(Invoice invoice, Guid? profileId, CancellationToken ct)
    {
        if (!Possible(invoice))
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: перестраивать нечего — читаемый вид строится только " +
                $"для файлов {FileKindCatalog.Words(FileKindCatalog.All.Where(kind => kind.Reading == FileReading.Rendition))}.");
        var rebuilt = await renditions.RebuildAsync(invoice.ScanBlobPath!, profileId, ct);
        if (rebuilt is ModuleRendition.Refused { OtherKind: true } other)
            throw new ConflictException($"{InvoiceEndpoints.Label(invoice)}: перестраивать нечего. {other.Reason}");
        return View(rebuilt, invoice);
    }

    /// <summary>
    /// Что образ значит для распознавания — одним вопросом к ядру. Не строит: вопрос задаёт каждое
    /// чтение состояния распознавания.
    /// </summary>
    /// <returns><c>WhyNot</c> — почему файл нельзя распознать из-за образа; <c>null</c> — образ не
    /// мешает (образа ещё нет — тоже не мешает: его построит само распознавание и причину отказа
    /// назовёт исходом; «файл другого вида» сюда не попадает — ядро такой отказ не запоминает).
    /// <c>Current</c> — путь построенного образа, как он есть сейчас; <c>null</c> — образа нет.</returns>
    public async Task<(string? WhyNot, string? Current)> StateAsync(Invoice invoice, CancellationToken ct) =>
        invoice.ScanBlobPath is not { } path ? (null, null)
        : await renditions.FindAsync(path, ct) switch
        {
            ModuleRendition.Built built => (null, built.Path),
            ModuleRendition.Refused { OtherKind: false } refused when Possible(invoice) =>
                ($"файл не приведён к читаемому виду. {refused.Reason.TrimEnd('.')}", null),
            _ => (null, null),
        };

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
            var other => throw new InvoiceImageRefusedException(
                (other as ModuleRendition.Refused)?.Reason ?? "Читаемого вида у файла нет."),
        };

        await using var stored = await blobs.OpenAsync(path, ct);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), mime, image);
    }

    /// <summary>
    /// Файл, который уходит движку КАК ЕСТЬ (PDF, изображение), — для проверки до постановки (issue
    /// #1344); <c>null</c> — движку уйдёт образ или файл не распознаётся вовсе.
    ///
    /// <para>Образ здесь нарочно не строится, в отличие от <see cref="ReadableAsync" />: построение — это
    /// конвертер, секунды в запросе нажатия. Образ — PDF нашего же конвертера, и его встретит проверка
    /// ядра в фоновой задаче.</para>
    /// </summary>
    public async Task<(byte[] Content, string Mime)?> AsIsAsync(Invoice invoice, CancellationToken ct)
    {
        var mime = FileKinds.Recorded(invoice.ScanMimeType);
        if (invoice.ScanBlobPath is not { } path || FileKindCatalog.Find(mime) is not { Reading: FileReading.AsIs })
            return null;

        await using var stored = await blobs.OpenAsync(path, ct);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), mime);
    }

    /// <summary>Образ потоком; <c>null</c> — образа нет.</summary>
    public async Task<Stream?> OpenAsync(Invoice invoice, CancellationToken ct) =>
        (await StateAsync(invoice, ct)).Current is { } path ? await blobs.OpenAsync(path, ct) : null;

    private static InvoiceImageView View(ModuleRendition rendition, Invoice invoice) => rendition switch
    {
        // Пометка о раскладке — при любом исходе: где её показать, решает экран.
        ModuleRendition.Built built => Laid(
            new(Built, built.Pages, built.Notes, built.Converter, built.BuiltAt, null, false,
                LayoutNote: built.Layout?.Note), built.Layout, invoice),
        // Файл другого вида — не отказ, а «образ не положен»: экран покажет его как умеет сам.
        ModuleRendition.Refused { OtherKind: false } refused =>
            new(Refused, null, [], null, null, refused.Reason, refused.AboutFile),
        _ => AsOriginal,
    };

    /// <summary>
    /// Раскладка — готовым состоянием ядра; параметров профиля модуль не толкует (issue #1456).
    ///
    /// <para>⚠️ «До профилей» — только у книги: запись без исхода раскладки вида файла не помнит, а Word и
    /// <c>.xls</c> перестроение по профилю не разложит — строка «применит профиль» была бы им неправдой.</para>
    ///
    /// <para>«Подставить не удалось» — по профилю, но без профиля и состояния: книга своей раскладки не
    /// задала, и выбор другого профиля перестроением имеет смысл; почему не вышло — пометка построителя.</para>
    /// </summary>
    private static InvoiceImageView Laid(InvoiceImageView view, ModuleRenditionLayout? layout, Invoice invoice) =>
        layout?.Outcome switch
        {
            ModuleLayoutOutcome.BookOwn => view with { Layout = ByFile },
            ModuleLayoutOutcome.Applied => view with
            {
                Layout = ByProfile,
                Profile = layout is { ProfileId: { } id, ProfileName: { } name } ? new(id, name) : null,
                ProfileState = layout.ProfileState switch
                {
                    ModuleLayoutProfileState.Current => "current",
                    ModuleLayoutProfileState.Changed => "changed",
                    ModuleLayoutProfileState.Deleted => "deleted",
                    _ => null,
                },
            },
            ModuleLayoutOutcome.BeforeProfiles when FileKinds.Recorded(invoice.ScanMimeType) == FileKinds.Xlsx =>
                view with { Layout = ByProfile, ProfileState = "none" },
            ModuleLayoutOutcome.Skipped => view with { Layout = ByProfile },
            _ => view,
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
    /// «Перестроить» — построить образ заново тем конвертером, что стоит сейчас; книгу без своей
    /// раскладки — по профилю печати из тела (issue #1456), без тела — по профилю прежнего вида.
    ///
    /// <para>⚠️ Версию счёта (<c>If-Match</c>) адрес не спрашивает: в счёт он не пишет — образ лежит
    /// у ядра. По той же причине его не останавливает и запертый счёт: документ закрытого периода —
    /// оригинал, а он остаётся тем же.</para>
    /// </summary>
    private static async Task<Ok<InvoiceImageView>> RebuildAsync(
        Guid id, InvoiceImageRebuild? request, CostsDbContext db, InvoiceScanImage image, CancellationToken ct) =>
        TypedResults.Ok(await image.RebuildAsync(
            await InvoiceEndpoints.FindAsync(db, id, ct), request?.PrintProfileId, ct));
}
