using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Черновик, заведённый из скана: сам счёт и то, что известно о его распознавании.</summary>
public sealed record InvoiceFromScanView(InvoiceView Invoice, InvoiceRecognitionView Recognition);

/// <summary>
/// Путь «скан → черновик счёта» (ТЗ COST-8, COST-6.2; задача B1b, issue #1077).
///
/// <para><b>Состояние распознавания — своим адресом, а не полем счёта.</b> Пока скан читается, форма
/// спрашивает о ходе раз в несколько секунд; тянуть ради этого счёт целиком — со строками, разноской
/// и опросом ссылок — незачем. И версии счёта этот ответ не несёт: распознавание, которое не удалось,
/// счёт не меняло.</para>
/// </summary>
public static class InvoiceRecognitionEndpoints
{
    private const string Read = "costs.invoice.read";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices").WithTags("Счета на оплату");

        group.MapPost("/from-scan", FromScanAsync)
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit))
            .DisableAntiforgery();
        group.MapGet("/{id:guid}/recognition", GetAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapPost("/{id:guid}/recognition", StartAsync)
            .RequireAuthorization(AppPolicies.Permission(CostsModule.InvoiceEdit));
    }

    /// <summary>
    /// Завести черновик из файла и поставить его распознавание.
    ///
    /// <para>Один запрос, а не «создать, потом приложить»: оборвись связь между двумя, остался бы
    /// пустой черновик без скана — запись ни о чём, которую некому убрать.</para>
    ///
    /// <para>Черновик заводится ВСЕГДА, когда файл годен: распознавание — помощь, а не условие.
    /// Не настроено или не удалось — черновик со сканом остаётся, и человек заполняет его руками;
    /// причина видна в ответе.</para>
    /// </summary>
    private static async Task<Created<InvoiceFromScanView>> FromScanAsync(
        IFormFile file, CostsDbContext db, IModuleTypes types, IModuleUser user, IModuleBlobs blobs,
        IModuleActivityLog log, InvoiceDesk desk, InvoiceScanRecognition scan, ILoggerFactory logs,
        CancellationToken ct)
    {
        if (file.Length == 0)
            throw new InvalidRequestException("Файл пуст — распознавать и прикладывать нечего.");
        // Предел назван словами и здесь, а не только на клиенте: без него файл между этим числом и
        // пределом тела запроса проходил бы мимо экрана, который его отвергает (issue #1093).
        if (file.Length > InvoiceScanRecognition.MaxScanBytes)
            throw new InvalidRequestException(
                $"Файл больше {InvoiceScanRecognition.MaxScanBytes / (1024 * 1024)} МБ — счёт из такого скана не заводится.");
        if (!InvoiceScanRecognition.IsReadable(file.ContentType))
            throw new InvalidRequestException(
                "Из скана счёт заводится по PDF, PNG или JPEG. Файл другого вида можно приложить к счёту, " +
                "заведённому вручную, — но распознать его нечем.");

        var typeId = await types.FindAsync(CostsRecordTypes.InvoiceCode, ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.InvoiceCode}» в системе не заведён, поэтому счёт завести нечем. Тип " +
                "заводит запуск приложения — причину пропуска он называет в журнале запуска.");

        string path;
        await using (var content = file.OpenReadStream())
            path = await blobs.PutAsync(file.FileName, content, file.ContentType, ct);

        var invoice = Invoice.Create(typeId, user.Id);
        try
        {
            invoice.AttachScan(path, file.FileName, file.ContentType, file.Length);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Счёт не сохранился — файл остался бы в хранилище ничьим. Уборка причину не подменяет.
            try { await blobs.DeleteAsync(path, CancellationToken.None); }
            catch (Exception) { }
            throw;
        }

        // ⚠️ Черновик со сканом ЗАВЕДЁН — с этой строки ответом может быть только он. Отказ после
        // сохранения оставил бы человека с ошибкой на экране и счётом, о котором он не знает: он
        // загрузил бы файл ещё раз, и в реестре стояли бы два черновика с одним сканом. Поэтому дальше —
        // без токена отмены (оборванный запрос не бросает постановку на полпути) и без отказов наружу:
        // что не удалось, названо в журнале сервера, а состояние распознавания уходит в ответе как есть.
        var none = CancellationToken.None;
        try
        {
            await log.RecordAsync(InvoiceActions.Created, invoice.Id.ToString(), InvoiceEndpoints.Label(invoice),
                after: $"из скана: {file.FileName}", ct: none);
        }
        catch (Exception lost)
        {
            logs.CreateLogger(typeof(InvoiceRecognitionEndpoints)).LogError(lost,
                "Счёт {InvoiceId} заведён из скана, но запись об этом в журнал действий не легла.", invoice.Id);
        }

        try
        {
            await scan.StartAsync(invoice, none);
        }
        catch (DomainException)
        {
            // Очередь отказала в постановке. Причина не потеряна: постановка записала её исходом.
        }
        catch (Exception crash)
        {
            // Не наш отказ: профиль не объявлен, у операции нет исполнителя, сбой базы. Распознавание
            // не поставлено — форма покажет «не запускалось» или «прервано», и запустить можно снова.
            logs.CreateLogger(typeof(InvoiceRecognitionEndpoints)).LogError(crash,
                "Счёт {InvoiceId} заведён из скана, но распознавание поставить не удалось.", invoice.Id);
        }

        return TypedResults.Created($"/api/costs/invoices/{invoice.Id}",
            new InvoiceFromScanView(await desk.ViewAsync(invoice, none), await scan.ViewAsync(invoice, none)));
    }

    private static async Task<Ok<InvoiceRecognitionView>> GetAsync(
        Guid id, CostsDbContext db, InvoiceScanRecognition scan, CancellationToken ct) =>
        TypedResults.Ok(await scan.ViewAsync(await InvoiceEndpoints.FindAsync(db, id, ct), ct));

    /// <summary>
    /// Распознать приложенный скан — впервые или ещё раз после отказа.
    ///
    /// <para>⚠️ Версию счёта (<c>If-Match</c>) этот адрес не спрашивает, хотя стоит под «/invoices/{id}/».
    /// Счёт он не меняет — пишет только запись о распознавании; а когда прочитанное ляжет в счёт,
    /// версии формы уже не будет: задача фоновая. Занятые поля она не трогает, поэтому устаревшая
    /// форма здесь ничего не теряет.</para>
    /// </summary>
    private static async Task<Ok<InvoiceRecognitionView>> StartAsync(
        Guid id, CostsDbContext db, InvoiceScanRecognition scan, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        await scan.StartAsync(invoice, ct);
        return TypedResults.Ok(await scan.ViewAsync(invoice, ct));
    }
}
