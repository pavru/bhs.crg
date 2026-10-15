using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Удаление счёта (issue #1282): удаляется только черновик без оплаты — насовсем, вместе со строками,
/// разноской, записью распознавания и файлом.
///
/// <para><b>Сторож задачи</b> — отказы: разобранный, отклонённый, оплаченный (рядом с оплатой, в
/// <see cref="InvoicePaymentTests" />), устаревшая версия (перепись адресов в
/// <see cref="InvoiceStaleFormTests" /> находит новый адрес сама) и человек без права правки. Ломается он
/// снятием проверки состояния: тогда удалением убирали бы настоящие счета, а не ошибочные.</para>
/// </summary>
public sealed class InvoiceDeletionTests(InvoiceScanHost host)
    : InvoiceLineTestBase(host), IClassFixture<InvoiceScanHost>
{
    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid invoice) =>
        client.DeleteAsync($"/api/costs/invoices/{invoice}");

    private async Task<string> AttachAsync(HttpClient client, Guid invoice, string fileName)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"%PDF-1.4 {Guid.NewGuid():N}"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/scan",
            new MultipartFormDataContent { { file, "file", fileName } }));
        return (await StoredAsync(invoice))!.ScanBlobPath!;
    }

    private async Task<Invoice?> StoredAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Invoices
            .AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoice);
    }

    private async Task<List<ActivityRecord>> JournalAsync(Guid invoice, string action)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == invoice.ToString())];
    }

    /// <summary>
    /// Главный путь: заведённый по ошибке черновик удаляется — его нет ни в реестре, ни в базе вместе со
    /// строками и разноской, файл из хранилища убран, а в журнале осталась запись, по которой удалённое
    /// можно опознать: номер, дата, поставщик, имя файла — и без суммы.
    /// </summary>
    [Fact]
    public async Task Черновик_удаляется_насовсем_вместе_со_строками_и_файлом()
    {
        var (client, _) = await SignInAsync("Admin");
        var (site, _) = await SiteAsync("Удаление счёта");
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 2, price: 5_678m)]);
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 2)]);
        var path = await AttachAsync(client, invoice, "Ошибочный счёт.pdf");
        var number = (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Номер").GetString()!;

        var deleted = await DeleteAsync(client, invoice);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/costs/invoices/{invoice}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/costs/invoices/{invoice}/scan")).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        Assert.DoesNotContain(list.EnumerateArray(), i => i.GetProperty("id").GetGuid() == invoice);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            Assert.False(await db.InvoiceLines.AnyAsync(l => l.InvoiceId == invoice));
            Assert.False(await db.InvoiceAllocations.AnyAsync(a => a.InvoiceId == invoice));
            // Файл убран из хранилища: путь больше не открывается.
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IModuleBlobs>().SizeAsync(path));
        }

        var record = Assert.Single(await JournalAsync(invoice, InvoiceActions.Deleted.Code));
        Assert.Contains(number, record.TargetLabel);
        Assert.Contains("поставщик: ООО «Кабель-Торг»", record.Before);
        Assert.Contains("файл: Ошибочный счёт.pdf", record.Before);
        Assert.DoesNotContain("5678", (record.Before + record.After + record.TargetLabel).Replace(" ", ""));
        // История удалённого счёта находится по тому же targetId: запись о создании на месте.
        Assert.Single(await JournalAsync(invoice, InvoiceActions.Created.Code));

        // Повтор отвечает «нет такого» — клиент читает это как «уже удалён».
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(client, invoice)).StatusCode);
    }

    /// <summary>
    /// Черновик без оплаты, но с книгой исправлений (оплаченный исправили, оплату отменили, вернули в
    /// черновик), не удаляется: отказ словами, а не 500 от внешнего ключа книги (E2, spec п. 1.5).
    /// </summary>
    [Fact]
    public async Task Счёт_с_книгой_исправлений_не_удаляется()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            await db.InOpenPeriodAsync(scope.ServiceProvider.GetRequiredService<IModulePeriods>(), async boundaries =>
            {
                var stored = await db.Invoices.SingleAsync(i => i.Id == invoice);
                await InvoiceBook.AppendAsync(db, stored, new BookChange(InvoiceBookKind.Unpayment, [], 1.00m, [], 1.00m),
                    boundaries, new DateOnly(2026, 10, 5), new BookAuthor(null), default);
                await db.SaveChangesAsync();
            });
        }

        var refused = await DeleteAsync(client, invoice);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("книга исправлений", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(await StoredAsync(invoice));
        Assert.Empty(await JournalAsync(invoice, InvoiceActions.Deleted.Code));
    }

    /// <summary>
    /// Разобранный и отклонённый не удаляются: отказ называет выход — вернуть в черновик. Вернули —
    /// удаляется. Так разобранный неоплаченный уходит в два действия (решение владельца 11.10.2026).
    /// </summary>
    [Theory]
    [InlineData("parsed", "разобран")]
    [InlineData("rejected", "отклонён")]
    public async Task Удаляется_только_черновик(string state, string named)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/{state}", null));

        var refused = await DeleteAsync(client, invoice);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var why = await refused.Content.ReadAsStringAsync();
        Assert.Contains(named, why);
        Assert.Contains("Верните счёт в черновик", why);
        Assert.NotNull(await StoredAsync(invoice));
        Assert.Empty(await JournalAsync(invoice, InvoiceActions.Deleted.Code));

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null));
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, invoice)).StatusCode);
    }

    /// <summary>
    /// Отдельного права на удаление нет: удаляет тот, кто правит счета. Читающий — не удаляет.
    /// </summary>
    [Fact]
    public async Task Без_права_правки_счёт_не_удалить()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var created = await admin.PostAsJsonAsync("/api/roles", new
        {
            title = $"Читатель счетов {Guid.NewGuid():N}",
            summary = "Проверка права на удаление счёта",
            permissions = new[] { "costs.invoice.read" },
        });
        await OkAsync(created);
        var role = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        var (reader, _) = await SignInAsync(role);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/costs/invoices/{invoice}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(reader, invoice)).StatusCode);

        Assert.NotNull(await StoredAsync(invoice));
        // А тот, у кого право правки есть (роль снабженца), удаляет — без какого-либо особого права.
        var (supplierUser, _) = await SignInAsync("Supplier");
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(supplierUser, invoice)).StatusCode);
    }

    /// <summary>
    /// Отказ хранилища — не отказ удаления: счёта уже нет, и ответ «сбой» заставил бы человека искать
    /// счёт, которого нет. Файл остаётся осиротевшим — его подберёт обычная уборка.
    /// </summary>
    [Fact]
    public async Task Отказ_хранилища_не_делает_удаление_сбоем()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await AttachAsync(client, invoice, "Файл остаётся.pdf");

        HttpResponseMessage deleted;
        host.BlobDeleteFailure = new IOException("хранилище не ответило");
        try
        {
            deleted = await DeleteAsync(client, invoice);
        }
        finally
        {
            host.BlobDeleteFailure = null;
        }

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await StoredAsync(invoice));
        Assert.Single(await JournalAsync(invoice, InvoiceActions.Deleted.Code));
    }

    /// <summary>
    /// Отказ журнала — тоже не отказ удаления (ревью PR #1298): счёт удалён насовсем, и ответ «сбой»
    /// открыл бы диалог «Счёт не удалён» над счётом, которого нет. Файл при этом убран.
    /// </summary>
    [Fact]
    public async Task Отказ_журнала_не_делает_удаление_сбоем()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var path = await AttachAsync(client, invoice, "Журнал молчит.pdf");

        HttpResponseMessage deleted;
        host.JournalFailureFor = InvoiceActions.Deleted.Code;
        try
        {
            deleted = await DeleteAsync(client, invoice);
        }
        finally
        {
            host.JournalFailureFor = null;
        }

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await StoredAsync(invoice));
        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IModuleBlobs>().SizeAsync(path));
    }

    /// <summary>
    /// Удалённый счёт можно завести заново с теми же поставщиком, номером и датой: уникальности по
    /// реквизитам в базе нет, дубликат — предупреждение, а не запрет (ТЗ COST-6.2). И предупреждения у
    /// нового счёта нет: дублем ему быть уже не с кем.
    /// </summary>
    [Fact]
    public async Task Удалённый_счёт_заводится_заново_с_теми_же_реквизитами()
    {
        var (client, _) = await SignInAsync("Admin");
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = $"ПОВТОР-{Guid.NewGuid().ToString()[..6]}",
            ["Дата"] = "2026-10-01",
            ["Поставщик"] = Reference(supplier),
        };
        async Task<JsonElement> EnterAsync()
        {
            var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
            await OkAsync(response);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var first = (await EnterAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, first)).StatusCode);

        var again = await EnterAsync();

        Assert.NotEqual(first, again.GetProperty("id").GetGuid());
        Assert.Equal(0, again.GetProperty("duplicates").GetArrayLength());
    }

    // ── Распознавание и удаление ──────────────────────────────────────────────

    /// <summary>
    /// Идущее распознавание удалению не мешает: счёт удаляется, пока его файл читается, прочитанное
    /// выбрасывается, а задача завершается БЕЗ ошибки. Раньше запись об отказе искала строку, ушедшую
    /// каскадом, и падала сама — задача краснела словами «последовательность не содержит элементов».
    /// </summary>
    [Fact]
    public async Task Счёт_удаляется_пока_его_файл_читается_и_задача_не_падает()
    {
        var (client, _) = await SignInAsync("Admin");
        var scan = InvoiceFromScanTests.Scan();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Recognition.On(scan, async () =>
        {
            reading.TrySetResult();
            await gate.Task;
            return InvoiceFromScanTests.Read(InvoiceFromScanTests.Header(number: "УДАЛЯЕМЫЙ-1"));
        });

        Guid job;
        try
        {
            var created = await InvoiceFromScanTests.FromScanAsync(client, scan, "Не тот файл.pdf");
            var invoice = created.GetProperty("invoice").GetProperty("id").GetGuid();
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using (var scope = host.Services.CreateScope())
                job = (await scope.ServiceProvider.GetRequiredService<CostsDbContext>().InvoiceRecognitions
                    .AsNoTracking().FirstAsync(r => r.InvoiceId == invoice)).JobId
                    ?? (await client.GetFromJsonAsync<JsonElement>("/api/jobs/active")).EnumerateArray()
                        .Single(j => j.GetProperty("targetId").GetGuid() == invoice).GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, invoice)).StatusCode);
            Assert.Null(await StoredAsync(invoice));
        }
        finally
        {
            gate.TrySetResult();
        }

        var finished = await FinishedAsync(client, job);
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("error").ValueKind);
        Assert.NotEqual("Failed", finished.GetProperty("status").GetString());
    }

    /// <summary>
    /// Задача, дождавшаяся очереди уже после удаления счёта, завершается без ошибки: записи о
    /// распознавании нет — она ушла каскадом, — и это не «распознавать нечего», а удалённый счёт.
    /// </summary>
    [Fact]
    public async Task Задача_по_удалённому_счёту_завершается_без_ошибки()
    {
        using var scope = host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<InvoiceScanReading>()
            .RunAsync(Guid.NewGuid(), Guid.NewGuid(), "invoices/нет-такого.pdf", CancellationToken.None);
    }

    /// <summary>
    /// Запуск распознавания одновременно с удалением отвечает «счёта нет», а не «файл уже распознаётся»:
    /// отказ базы на запись о распознавании здесь — исчезнувший счёт, а не второй запуск.
    /// </summary>
    [Fact]
    public async Task Запуск_распознавания_по_исчезнувшему_счёту_называет_причину()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await AttachAsync(client, invoice, "Счёт.pdf");
        // Запуск прочитал счёт — и тут счёт удалили.
        var read = (await StoredAsync(invoice))!;
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, invoice)).StatusCode);

        using var scope = host.Services.CreateScope();
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            scope.ServiceProvider.GetRequiredService<InvoiceScanRecognition>().StartAsync(read, CancellationToken.None));

        Assert.Contains("удалили", refusal.Message);
    }

    private static async Task<JsonElement> FinishedAsync(HttpClient client, Guid job)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var state = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job}");
            if (state.GetProperty("finishedAt").ValueKind != JsonValueKind.Null) return state;

            Assert.True(DateTime.UtcNow < deadline, $"Задача распознавания {job} так и не закончилась.");
            await Task.Delay(50);
        }
    }
}
