using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Support;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ось ACT: запуск долгих операций и наблюдение за их итогом (issue #898).
///
/// Проверяется сквозным путём — по HTTP и по MCP, — потому что оба входа обязаны вести в одно ядро
/// с одними защитами. Тест на уровне сервиса этого не отличил бы: он вызывал бы ядро напрямую и
/// прошёл бы даже тогда, когда адаптер зовёт что-то мимо.
///
/// Задачи заводятся ЗАПИСЬЮ В БАЗУ, а не постановкой в очередь: поставленная задача тут же уходит
/// фоновому сервису и завершается, когда ей вздумается, — проверка «повтор отвергается, пока идёт
/// первая» на этом была бы гонкой.
/// </summary>
[Collection("Integration")]
public class OperationLaunchTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Хост, в котором распознавать заведомо некому. Настоящий предполёт ходит к движку и зависит от
    /// машины: на стенде разработчика локальная модель может оказаться живой, и проверка «отказ
    /// доходит до вызывающего» стала бы зелёной или красной по не относящейся к делу причине.
    /// Сам предполёт проверяется отдельно; здесь важен только путь его отказа наружу.
    /// </summary>
    private sealed class FakePreflight(RecognitionBlock? block) : IRecognitionPreflight
    {
        /// <summary>Сколько раз спросили. Ноль на отвергнутом запуске = до движка дело не дошло.</summary>
        public int Calls { get; private set; }

        public Task<RecognitionBlock?> CheckAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(block);
        }
    }

    /// <summary>
    /// Хост с подставным предполётом и БЕЗ исполнителя фоновых задач.
    ///
    /// Исполнитель убран не для скорости: поставленная задача ушла бы распознавать по-настоящему —
    /// с обращением к движку и по сети. Здесь проверяется постановка, а не выполнение, и оставленный
    /// исполнитель превратил бы проверку в поход наружу.
    /// </summary>
    private WebApplicationFactory<Program> HostWith(FakePreflight preflight, IDocumentRecognizer? recognizer = null)
        => fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecognitionPreflight>();
            services.AddSingleton<IRecognitionPreflight>(preflight);
            services.RemoveJobRunner();
            if (recognizer is not null)
            {
                services.RemoveAll<IDocumentRecognizer>();
                services.AddSingleton(recognizer);
            }
        }));

    private static readonly RecognitionBlock NoEngines = new(
        RecognitionBlock.NoEngine, "Нет включённых и настроенных движков распознавания.");

    /// <summary>
    /// ГОСТ-набор с одним источником. <paramref name="manuallyEdited" /> — разбиение правил человек,
    /// и перезапись такого требует согласия.
    /// </summary>
    private static async Task<(Guid FileId, Guid SourceId)> SeedGostSourceAsync(
        IServiceProvider services, bool manuallyEdited)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        using var pdf = new SharpPdfDocument();
        pdf.AddPage();
        pdf.AddPage();
        using var bytes = new MemoryStream();
        pdf.Save(bytes, false);
        bytes.Position = 0;
        var blobPath = await blobStorage.UploadAsync("manual.pdf", bytes, "application/pdf");

        var file = DataSetFile.Create("Альбом", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var source = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        file.SetGrouping(JsonSerializer.Serialize(new GostGroupingData(
            [new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "План",
                [new GostGroupingPage(0, new Dictionary<string, string?>())])],
            ManuallyEdited: manuallyEdited)));

        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return (file.Id, source.Id);
    }

    private async Task<(HttpClient Client, Guid UserId)> AuthorizedClientAsync(
        WebApplicationFactory<Program>? factory = null)
    {
        var email = $"act_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Act";
        Guid userId;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Агент", EmailConfirmed = true,
            };
            var created = await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            // Роль обязательна: с переходом на права (issue #947) пользователь БЕЗ роли не имеет
            // ни одного права и получает 403 на всём. В живой системе такой учётной записи не
            // бывает — регистрация открыта только первому администратору, остальных заводит экран
            // пользователей и всегда с ролью, — так что заводить её здесь значило бы проверять
            // состояние, которого не существует.
            Assert.True((await users.AddToRoleAsync(user, BHS.CRG.Api.Auth.SystemRoles.IdEngineer)).Succeeded);
            userId = user.Id;
        }

        var client = (factory ?? (WebApplicationFactory<Program>)fixture).CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, userId);
    }

    /// <summary>Задача прямо в базе — минуя очередь, чтобы её состояние задавали мы, а не фон.</summary>
    private async Task<Guid> SeedJobAsync(Guid userId, Guid targetId, JobKind kind, Action<Job>? finish = null)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = Job.Create(kind, userId, targetId, "Задача");
        finish?.Invoke(job);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> SeedDocumentSetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        return (await m.Send(new CreateDocumentSetCommand(section.Id, "Комплект"))).Id;
    }

    // ── Наблюдение за задачей ──────────────────────────────────────────────────────

    /// <summary>
    /// Главное, ради чего заведён запрос по id: у ЗАВЕРШИВШЕЙСЯ задачи виден итог. До этого список
    /// активных был единственным входом, и завершённая задача из него просто исчезала — успех был
    /// неотличим от отказа. Оба ответа проверяются рядом: «видна по id» само по себе не доказывает
    /// ничего, она могла бы быть видна и в списке активных.
    /// </summary>
    [Fact]
    public async Task FinishedJob_IsReadableById_ThoughGoneFromActive()
    {
        var (client, userId) = await AuthorizedClientAsync();
        var jobId = await SeedJobAsync(userId, Guid.NewGuid(), JobKind.AssembleDocumentSet,
            job => job.Fail("Документ «АОСР» не готов."));

        var byId = await client.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        var job = await byId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Equal("Документ «АОСР» не готов.", job.GetProperty("error").GetString());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("finishedAt").ValueKind);

        var active = await client.GetFromJsonAsync<JsonElement>("/api/jobs/active");
        Assert.DoesNotContain(active.EnumerateArray(), j => j.GetProperty("id").GetGuid() == jobId);
    }

    /// <summary>
    /// Чужая задача — «не найдена», а не «нельзя»: отдельный код подтверждал бы её существование.
    /// Правило то же, что у отмены, и запрос по id не должен стать в нём дырой.
    /// </summary>
    [Fact]
    public async Task ForeignJob_IsNotFound()
    {
        var (_, ownerId) = await AuthorizedClientAsync();
        var jobId = await SeedJobAsync(ownerId, Guid.NewGuid(), JobKind.AssembleDocumentSet);

        var (stranger, _) = await AuthorizedClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    // ── Сборка комплекта ───────────────────────────────────────────────────────────

    /// <summary>
    /// Защиты от повторного запуска у сборки не было вовсе: экран прикрыт блокировкой кнопки, но она
    /// живёт во вкладке и не переживает перезагрузку. Две сборки писали бы один и тот же выход.
    /// </summary>
    [Fact]
    public async Task Assemble_IsRejected_WhileAnotherIsRunning()
    {
        var (client, userId) = await AuthorizedClientAsync();
        var setId = await SeedDocumentSetAsync();
        await SeedJobAsync(userId, setId, JobKind.AssembleDocumentSet);

        var response = await client.PostAsync($"/api/document-sets/{setId}/assemble", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("уже идёт", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Assemble_ReturnsNotFound_ForUnknownSet()
    {
        var (client, _) = await AuthorizedClientAsync();

        var response = await client.PostAsync($"/api/document-sets/{Guid.NewGuid()}/assemble", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Распознавание ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Предполёт движка переехал из обработчика эндпоинта в ядро — и обязан там срабатывать. В
    /// тестовом хосте движков нет ни одного, поэтому запуск отвергается ещё до поиска набора: это и
    /// есть заявленный порядок проверок, а не случайность. Отказ приходит своим кодом — по нему
    /// интерфейс отличает «не настроено» от «модель слепа».
    /// </summary>
    [Fact]
    public async Task Recognize_IsBlocked_WhenNoEngineConfigured()
    {
        using var host = HostWith(new FakePreflight(NoEngines));
        var (client, _) = await AuthorizedClientAsync(host);
        var (fileId, _) = await SeedGostSourceAsync(host.Services, manuallyEdited: false);

        var response = await client.PostAsync($"/api/datasets/files/{fileId}/recognize", null);

        // Отказ приходит своим кодом — по нему интерфейс отличает «не настроено» от «модель слепа»,
        // и совет «проверьте настройки» во втором случае отправил бы искать то, что и так на месте.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recognition_unavailable", body.GetProperty("code").GetString());
    }

    // ── Те же операции через MCP ───────────────────────────────────────────────────

    private static async Task<JsonElement> McpCallAsync(HttpClient client, string tool, object args)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name = tool, arguments = args },
                }, Json),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var payload = (response.Content.Headers.ContentType?.MediaType ?? "").Contains("event-stream")
            ? string.Concat(body.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l[5..].Trim()))
            : body;

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("result").Clone();
    }

    /// <summary>Текст, который увидит агент: содержимое первого текстового блока ответа.</summary>
    private static string TextOf(JsonElement result)
        => result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString() ?? "";

    /// <summary>
    /// Тот же итог, но глазами агента. Проверять надо именно здесь: инструмент — отдельный адаптер,
    /// и зелёный HTTP-тест о нём не говорит ничего.
    /// </summary>
    [Fact]
    public async Task McpGetJob_ReportsFailureWithReason()
    {
        var (client, userId) = await AuthorizedClientAsync();
        var jobId = await SeedJobAsync(userId, Guid.NewGuid(), JobKind.RecognizeGostSet,
            job => job.Fail("Движок не ответил."));

        var text = TextOf(await McpCallAsync(client, "get_job", new { jobId }));

        Assert.Contains("Failed", text);
        Assert.Contains("Движок не ответил.", text);
    }

    /// <summary>
    /// Неизвестная задача — ОШИБКА, а не пустой ответ. Найдено живой проверкой: инструмент возвращал
    /// пустое содержимое, и опрашивающий в цикле прочёл бы это как «ещё не готово», ожидая того,
    /// чего нет. Отсюда же правило про чужие задачи: они неотличимы от несуществующих.
    /// </summary>
    [Fact]
    public async Task McpGetJob_ReportsMissingJobAsError()
    {
        var (client, _) = await AuthorizedClientAsync();

        var result = await McpCallAsync(client, "get_job", new { jobId = Guid.NewGuid() });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("не найдена", TextOf(result));
    }

    /// <summary>
    /// Отказ обязан выглядеть отказом. Инструмент, вернувший «успешно» на неудавшийся запуск, — то
    /// самое «правдоподобно, но неверно», за которым агент строит рассуждение на пустом месте.
    /// </summary>
    [Fact]
    public async Task McpAssemble_ReportsConflict_WhileAnotherIsRunning()
    {
        var (client, userId) = await AuthorizedClientAsync();
        var setId = await SeedDocumentSetAsync();
        await SeedJobAsync(userId, setId, JobKind.AssembleDocumentSet);

        var result = await McpCallAsync(client, "assemble_document_set", new { setId });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("уже идёт", TextOf(result));
    }

    /// <summary>
    /// Порядок проверок — содержательный, а не косметический: предполёт может ждать холодную модель
    /// до полутора минут, и стой он первым, всё это время окно для второй такой же задачи оставалось
    /// бы открытым. Ноль обращений к предполёту и есть доказательство порядка; без него тест
    /// подтверждал бы только код ответа, который вышел бы тем же и при обратном порядке.
    /// </summary>
    [Fact]
    public async Task Recognize_ChecksRunningJob_BeforeAskingEngine()
    {
        var preflight = new FakePreflight(NoEngines);
        using var host = HostWith(preflight);
        var (client, userId) = await AuthorizedClientAsync(host);
        var (fileId, sourceId) = await SeedGostSourceAsync(host.Services, manuallyEdited: false);
        await SeedJobAsync(userId, fileId, JobKind.RecognizeGostSet);

        // Спрашиваем по ИСТОЧНИКУ, а занят НАБОР: распознавание ГОСТ работает над набором целиком,
        // и вход через источник обязан видеть ту же занятость — иначе защита обходится сменой входа.
        var response = await client.PostAsync($"/api/datasets/sources/{sourceId}/recognize", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, preflight.Calls);
    }

    // ── Подтверждение перезаписи ручной правки ─────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpRecognition_ManualGroupingDirectsToTheDatasetInWeb(bool bySource)
    {
        var preflight = new FakePreflight(null);
        using var host = HostWith(preflight);
        var (client, _) = await AuthorizedClientAsync(host);
        var (fileId, sourceId) = await SeedGostSourceAsync(host.Services, manuallyEdited: true);

        var tool = bySource ? "recognize_source" : "recognize_dataset";
        var args = bySource ? (object)new { sourceId } : new { datasetId = fileId };
        var result = await McpCallAsync(client, tool, args);

        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = TextOf(result);
        Assert.StartsWith("[manual_grouping]", text);
        Assert.Contains($"/datasets/files/{fileId}/grouping", text);
        Assert.Contains("Распознать заново", text);
        Assert.Equal(0, preflight.Calls);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Jobs.AnyAsync(j => j.TargetId == fileId));
        Assert.False(await db.ActivityRecords.AnyAsync(r => r.Action == ActivityActions.McpToolCalled.Code && r.After == tool));
        Assert.True(GostGroupingSerialization.Parse((await db.DataSetFiles.FindAsync(fileId))!.Grouping)!.ManuallyEdited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpRecognition_AutomaticGroupingStartsJobForTheDataset(bool bySource)
    {
        using var host = HostWith(new FakePreflight(null));
        var (client, userId) = await AuthorizedClientAsync(host);
        var (fileId, sourceId) = await SeedGostSourceAsync(host.Services, manuallyEdited: false);
        var tool = bySource ? "recognize_source" : "recognize_dataset";
        var args = bySource ? (object)new { sourceId } : new { datasetId = fileId };
        var result = await McpCallAsync(client, tool, args);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var reply = JsonDocument.Parse(TextOf(result));
        Assert.Equal(await AssertQueuedRecognitionAsync(fileId, userId, allowManualOverwrite: false),
            reply.RootElement.GetProperty("jobId").GetGuid());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ActivityRecords.SingleAsync(r => r.Action == ActivityActions.McpToolCalled.Code && r.After == tool);
        Assert.Equal(ActivityChannel.Mcp.Code, row.Channel);
        Assert.Equal(userId, row.ActorId);
        Assert.Equal((bySource ? sourceId : fileId).ToString(), row.TargetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebRecognition_ManualGroupingRequiresConfirmation(bool bySource)
    {
        using var host = HostWith(new FakePreflight(null));
        var (client, userId) = await AuthorizedClientAsync(host);
        var (fileId, sourceId) = await SeedGostSourceAsync(host.Services, manuallyEdited: true);
        var path = bySource ? $"/api/datasets/sources/{sourceId}/recognize" : $"/api/datasets/files/{fileId}/recognize";

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path, null)).StatusCode);
        var confirmed = await client.PostAsync(path + "?confirm=true", null);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        var body = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(await AssertQueuedRecognitionAsync(fileId, userId, allowManualOverwrite: true),
            body.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task McpRecognition_TableInManuallyGroupedDatasetIsRecognizedInPlace()
    {
        using var host = HostWith(new FakePreflight(null), new TableRecognizer());
        var (client, _) = await AuthorizedClientAsync(host);
        var (fileId, _) = await SeedGostSourceAsync(host.Services, manuallyEdited: true);
        Guid sourceId;
        Guid groupId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = (await db.DataSetFiles.FindAsync(fileId))!;
            var grouping = GostGroupingSerialization.Parse(file.Grouping)!;
            var group = Assert.Single(grouping.Groups) with { Id = groupId, Tags = [FunctionalTag.GostDocSpecification] };
            file.SetGrouping(JsonSerializer.Serialize(grouping with { Groups = [group] }));
            var source = file.AddSource("Спецификация", PdfProfiles.GostTableMarkerPrefix + groupId, "[]", 0);
            db.DataSetSources.Add(source);
            await db.SaveChangesAsync();
            sourceId = source.Id;
        }

        var result = await McpCallAsync(client, "recognize_source", new { sourceId });
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), TextOf(result));
        using var reply = JsonDocument.Parse(TextOf(result));
        Assert.True(reply.RootElement.GetProperty("completed").GetBoolean());

        using var read = fixture.Services.CreateScope();
        var stored = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await stored.Jobs.AnyAsync(j => j.TargetId == fileId));
        var saved = GostGroupingSerialization.Parse((await stored.DataSetFiles.FindAsync(fileId))!.Grouping)!;
        Assert.True(saved.ManuallyEdited);
        var savedGroup = Assert.Single(saved.Groups);
        Assert.Equal(groupId, savedGroup.Id);
        Assert.Equal(0, Assert.Single(savedGroup.Pages).PageIndex);
        Assert.Contains("Кабель", (await stored.DataSetSources.FindAsync(sourceId))!.CachedData);
    }

    private sealed class TableRecognizer : IDocumentRecognizer
    {
        public Task<RecognitionResult> RecognizeAsync(byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
        {
            Assert.Equal("application/pdf", mimeType);
            var row = new Dictionary<string, string> { [fields[0].Path] = "Кабель" };
            return Task.FromResult(new RecognitionResult(new Dictionary<string, string?>
            {
                ["Строки"] = JsonSerializer.Serialize(new[] { row }),
            }, null));
        }
    }

    private async Task<Guid> AssertQueuedRecognitionAsync(Guid fileId, Guid userId, bool allowManualOverwrite)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.SingleAsync(j => j.Kind == JobKind.RecognizeGostSet);
        Assert.Equal(fileId, job.TargetId);
        Assert.Equal(userId, job.UserId);
        Assert.Equal(JobStatus.Queued, job.Status);
        using var payload = JsonDocument.Parse(job.Payload!);
        Assert.Equal(1, payload.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(allowManualOverwrite, payload.RootElement.GetProperty("allowManualOverwrite").GetBoolean());
        var file = await db.DataSetFiles.FindAsync(fileId);
        Assert.Equal(file!.RecognitionRevision, payload.RootElement.GetProperty("expectedRevision").GetGuid());
        return job.Id;
    }

    /// <summary>
    /// Цель у задач общая, а операции разные: комплект — цель и сборки, и отправки почтой, и сверки
    /// качества. Пока вид не спрашивали, запущенная сверка (минуты) отвергала бы сборку — сообщением
    /// про сборку, которой не существует. Отказ, называющий несуществующую причину, хуже отсутствия
    /// отказа: искать будут не там.
    /// </summary>
    [Fact]
    public async Task Assemble_IsAllowed_WhileAnotherKindOfJobRunsOnTheSameSet()
    {
        var (client, userId) = await AuthorizedClientAsync();
        var setId = await SeedDocumentSetAsync();
        await SeedJobAsync(userId, setId, JobKind.SendEmail);

        var response = await client.PostAsync($"/api/document-sets/{setId}/assemble", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>
    /// Неверный идентификатор обязан выглядеть неверным идентификатором. Пока предполёт стоял до
    /// поиска цели, ответом было «распознавать некому» — и разбираться шли с настройками движка,
    /// которые в порядке. Для агента это тупик вдвойне: инструмент сам говорит, что повторять
    /// бессмысленно.
    /// </summary>
    [Fact]
    public async Task Recognize_ReportsUnknownDataset_NotEngineBlock()
    {
        using var host = HostWith(new FakePreflight(NoEngines));
        var (client, _) = await AuthorizedClientAsync(host);

        var response = await client.PostAsync($"/api/datasets/files/{Guid.NewGuid()}/recognize", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Защита движка одинакова на обоих входах — иначе агент прошёл бы там, где человек нет.</summary>
    [Fact]
    public async Task McpRecognize_ReportsEngineBlock()
    {
        using var host = HostWith(new FakePreflight(NoEngines));
        var (client, _) = await AuthorizedClientAsync(host);
        var (datasetId, _) = await SeedGostSourceAsync(host.Services, manuallyEdited: false);

        var result = await McpCallAsync(client, "recognize_dataset", new { datasetId });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("движков распознавания", TextOf(result));
        // Машинный код — вместе с текстом: по нему агент отличает «не настроено» от «модель слепа»,
        // не разбирая русскую фразу. По HTTP он приходит отдельным полем.
        Assert.Contains(RecognitionBlock.NoEngine, TextOf(result));
    }
}
