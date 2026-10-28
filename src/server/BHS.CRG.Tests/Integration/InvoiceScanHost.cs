using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост проверок пути «скан → черновик» (B1b, issue #1077): тот же, что у строк счёта (та же база, тот
/// же состав модулей), но распознаёт в нём сценарий теста, а не движок.
///
/// <para>Заменяется именно ПОРТ <see cref="IModuleRecognition" />, а не движок под ним: что порт делает
/// с движком, проверяют <c>ModuleRecognitionPortTests</c>. Здесь проверяется то, что модуль делает с
/// ответом порта, — и очередь, обработчик, связка записи и адреса при этом настоящие.</para>
/// </summary>
public sealed class InvoiceScanHost : InvoiceLineHost
{
    public ScriptedRecognition Recognition { get; } = new();

    /// <summary>
    /// Что сделать ОДИН раз в ту минуту, когда слияние уже прочитало счёт, но ещё не сохранило: так
    /// тест встаёт ровно между чтением и записью — туда, куда попадает правка формы, пришедшая
    /// одновременно с исходом распознавания. Точка — вызов охраны записи: она стоит посреди слияния.
    /// </summary>
    public Func<Task>? BetweenReadAndSave { get; set; }

    /// <summary>
    /// Что видела охрана записи — присланные ей реквизиты, по порядку; <c>null</c> — не записывать. Так
    /// тест узнаёт, судила ли охрана то, что легло в счёт (ревью PR #1329: отсрочка из условий).
    /// </summary>
    public List<string>? GuardSaw { get; set; }

    /// <summary>
    /// Отказ справочника на вопрос «что это за записи» (<see cref="IModuleCatalog.RefsAsync" />) — пока
    /// стоит. Так тест проверяет, что необязательный вопрос о запомненном не губит распознавание. Тест
    /// ставит и обязан снять: хост у класса общий.
    /// </summary>
    public Exception? RefsFailure { get; set; }

    /// <summary>
    /// Конвертер офисных файлов — подставной (issue #1270): настоящий живёт в контейнере, и его
    /// проверяет <c>deploy/converter.tests.sh</c>. По умолчанию звать его не должны: счёт с PDF
    /// или картинкой до конвертера не доходит. Тест ставит и обязан снять.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Converter { get; set; } =
        (request, ct) => Renditions.RenditionFixtures.MustNotBeCalled(request, ct);

    /// <summary>
    /// Сбой самой службы образов — пока стоит: не отказ построить (он приходит ответом), а
    /// исключение, как от базы или хранилища. Тест ставит и обязан снять.
    /// </summary>
    public Exception? RenditionsFailure { get; set; }

    /// <summary>
    /// Отказ хранилища на удаление файла — пока стоит (issue #1282): так проверяется, что удаление счёта
    /// не отвечает сбоем, когда счёт уже удалён, а файл убрать не удалось. Тест ставит и обязан снять.
    /// </summary>
    public Exception? BlobDeleteFailure { get; set; }

    /// <summary>
    /// Отказ хранилища на чтение файла модулем — пока стоит (ревью PR #1645): так проверяется, что сбой
    /// чтения или пропавший файл не отменяют постановку распознавания. Тест ставит и обязан снять.
    /// </summary>
    public Exception? BlobOpenFailure { get; set; }

    /// <summary>
    /// Отказ журнала действий на запись с этим кодом — пока стоит (ревью PR #1298): удаление, которое
    /// состоялось, не вправе ответить сбоем из-за журнала. Тест ставит и обязан снять.
    /// </summary>
    public string? JournalFailureFor { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleRecognition>();
            services.AddSingleton<IModuleRecognition>(Recognition);

            services.RemoveAll<BHS.CRG.Infrastructure.Renditions.OfficeConverterClient>();
            services.AddSingleton(_ => Renditions.RenditionFixtures.Client(
                (request, ct) => Converter(request, ct), version: "8.37.0"));

            // Порты файлов и образов — по ключу модуля (ТЗ CORE-37.2): подмена встаёт под тот же ключ, что
            // и настоящий порт, иначе адреса costs своего порта не нашли бы.
            const string costs = BHS.CRG.Modules.Costs.CostsModule.ModuleCode;
            services.RemoveAll<IModuleRenditions>();
            services.AddKeyedScoped<IModuleRenditions>(costs, (sp, _) => new FailingRenditions(new ModuleRenditionsPort(
                sp.GetRequiredService<BHS.CRG.Api.Renditions.RenditionStore>(), costs,
                sp.GetRequiredService<ILogger<ModuleRenditionsPort>>()), this));

            services.RemoveAll<IModuleBlobs>();
            services.AddKeyedScoped<IModuleBlobs>(costs, (sp, _) => new FailingBlobs(
                new ModuleBlobsPort(sp.GetRequiredService<IBlobStorage>(), costs), this));

            services.RemoveAll<IModuleActivityLog>();
            services.AddScoped<ModuleActivityLogPort>();
            services.AddScoped<IModuleActivityLog>(sp => new FailingJournal(sp.GetRequiredService<ModuleActivityLogPort>(), this));

            services.RemoveAll<IModuleWriteGuard>();
            services.AddScoped<ModuleWriteGuardPort>();
            services.AddScoped<IModuleWriteGuard>(sp => new InterruptedGuard(sp.GetRequiredService<ModuleWriteGuardPort>(), this));

            services.RemoveAll<IModuleCatalog>();
            services.AddScoped<ModuleCatalogPort>();
            services.AddScoped<IModuleCatalog>(sp => new FailingCatalog(sp.GetRequiredService<ModuleCatalogPort>(), this));
        });
    }

    /// <summary>Настоящий справочник — с отказом на вопрос о записях по просьбе теста.</summary>
    private sealed class FailingRenditions(IModuleRenditions inner, InvoiceScanHost host) : IModuleRenditions
    {
        public Task<ModuleRendition> EnsureAsync(string originalPath, CancellationToken ct = default) =>
            host.RenditionsFailure is { } failure ? Task.FromException<ModuleRendition>(failure) : inner.EnsureAsync(originalPath, ct);

        public Task<ModuleRendition?> FindAsync(string originalPath, CancellationToken ct = default) =>
            inner.FindAsync(originalPath, ct);

        public Task<ModuleRendition> RebuildAsync(
            string originalPath, Guid? layoutProfileId = null, CancellationToken ct = default) =>
            inner.RebuildAsync(originalPath, layoutProfileId, ct);
    }

    /// <summary>Настоящий журнал — с отказом на одно действие по просьбе теста.</summary>
    private sealed class FailingJournal(IModuleActivityLog inner, InvoiceScanHost host) : IModuleActivityLog
    {
        public Task RecordAsync(ModuleActivityAction action, string? targetId = null, string? targetLabel = null,
            string? before = null, string? after = null, CancellationToken ct = default) =>
            host.JournalFailureFor == action.Code
                ? Task.FromException(new InvalidOperationException("журнал не ответил"))
                : inner.RecordAsync(action, targetId, targetLabel, before, after, ct);
    }

    /// <summary>Настоящее хранилище — с отказом на удаление по просьбе теста.</summary>
    private sealed class FailingBlobs(IModuleBlobs inner, InvoiceScanHost host) : IModuleBlobs
    {
        public Task<string> PutAsync(string fileName, Stream content, string contentType, CancellationToken ct = default) =>
            inner.PutAsync(fileName, content, contentType, ct);

        public Task<Stream> OpenAsync(string path, CancellationToken ct = default) =>
            host.BlobOpenFailure is { } failure ? Task.FromException<Stream>(failure) : inner.OpenAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            host.BlobDeleteFailure is { } failure ? Task.FromException(failure) : inner.DeleteAsync(path, ct);

        public Task<long?> SizeAsync(string path, CancellationToken ct = default) => inner.SizeAsync(path, ct);
    }

    private sealed class FailingCatalog(IModuleCatalog inner, InvoiceScanHost host) : IModuleCatalog
    {
        public Task<IReadOnlyList<ModuleCatalogEntry>?> ListAsync(
            string entityType, RecordsFor purpose, CancellationToken ct = default) =>
            inner.ListAsync(entityType, purpose, ct);

        public Task<ModuleCatalogEntry?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task<ModuleCatalogChoice?> SearchAsync(
            string entityType, string? query, int limit, CancellationToken ct = default) =>
            inner.SearchAsync(entityType, query, limit, ct);

        public Task<IReadOnlyList<ModuleCatalogRef>?> RefsAsync(
            string entityType, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            host.RefsFailure is { } failure && entityType == CostsDirectories.Nomenclature
                ? Task.FromException<IReadOnlyList<ModuleCatalogRef>?>(failure)
                : inner.RefsAsync(entityType, ids, ct);

        public Task<ModuleCatalogFieldValues?> FieldValuesAsync(
            string entityType, string fieldKey, RecordsFor purpose, CancellationToken ct = default) =>
            inner.FieldValuesAsync(entityType, fieldKey, purpose, ct);

        public Task<IReadOnlyList<ModuleLineCandidates>?> SimilarAsync(
            string entityType, IReadOnlyList<ModuleLineWords> lines, int limit, CancellationToken ct = default) =>
            inner.SimilarAsync(entityType, lines, limit, ct);

        public Task<ModuleCatalogCard?> CardAsync(
            string entityType, Guid id, IReadOnlyList<string> fieldTags, CancellationToken ct = default) =>
            inner.CardAsync(entityType, id, fieldTags, ct);
    }

    /// <summary>
    /// Записи журнала об этом счёте — прочитанные ПОСЛЕ конца фоновой задачи.
    ///
    /// <para>Журнал пишется после исхода и вне его: форма видит «done» на мгновение раньше записи. Чтение
    /// сразу за исходом через раз давало ноль там, где ждали запись, — и, что хуже, давало бы ноль там,
    /// где ждали НОЛЬ, при любом поведении (ревью PR #1263). Конец задачи — знак, после которого записи
    /// уже не появится: журнал она пишет последним.</para>
    /// </summary>
    public async Task<IReadOnlyList<BHS.CRG.Application.Activity.ActivityRow>> JournalAsync(
        HttpClient client, Guid invoice, string action)
    {
        using var scope = Services.CreateScope();
        var job = (await scope.ServiceProvider.GetRequiredService<CostsDbContext>().InvoiceRecognitions
            .AsNoTracking().FirstAsync(r => r.InvoiceId == invoice)).JobId
            ?? throw new InvalidOperationException($"У распознавания счёта {invoice} нет номера задачи.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job}")).GetProperty("finishedAt").ValueKind
               == JsonValueKind.Null)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Задача распознавания счёта {invoice} так и не закончилась.");
            await Task.Delay(50);
        }

        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, action);
        return [.. records.Where(r => r.TargetId == invoice.ToString())];
    }

    /// <summary>Настоящая охрана записи — с остановкой перед ней по просьбе теста.</summary>
    private sealed class InterruptedGuard(IModuleWriteGuard inner, InvoiceScanHost host) : IModuleWriteGuard
    {
        public async Task<IReadOnlyList<ModuleWriteRefusal>> RefusalsAsync(
            Guid typeId, string? storedJson, string incomingJson, CancellationToken ct = default)
        {
            if (host.BetweenReadAndSave is { } interference)
            {
                host.BetweenReadAndSave = null;
                await interference();
            }

            host.GuardSaw?.Add(incomingJson);
            return await inner.RefusalsAsync(typeId, storedJson, incomingJson, ct);
        }
    }

    /// <summary>
    /// Распознавание по сценарию: ответ назначается СОДЕРЖИМОМУ файла. Так сценарий не зависит от
    /// порядка тестов и не протекает между ними — у каждого теста свой файл.
    /// </summary>
    public sealed class ScriptedRecognition : IModuleRecognition
    {
        private readonly ConcurrentDictionary<string, Func<Task<ModuleRecognitionResult>>> answers = new();

        /// <summary>Отказ готовности — «распознавать некому». Тест ставит и обязан снять.</summary>
        public RecognitionRefusedException? NotReady { get; set; }

        public void On(string content, Func<Task<ModuleRecognitionResult>> answer) => answers[content] = answer;

        /// <summary>С каким видом файл пришёл движку: «содержимое → вид» (issue #1265).</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Kinds { get; } = new();

        /// <summary>
        /// Файлы, которые «не читаются»: содержимое → слова отказа. Проверку файла порт делает настоящую
        /// (<c>ModuleRecognitionPortTests</c>); здесь важно лишь, КОГДА модуль её спрашивает (issue #1344).
        /// </summary>
        public ConcurrentDictionary<string, string> Unreadable { get; } = new();

        /// <summary>Сколько раз спрошена готовность — тест сверяет разницу до и после запуска.</summary>
        public int ReadyChecks => readyChecks;
        private int readyChecks;

        public Task EnsureReadableAsync(string profileCode, byte[] content, string mimeType, CancellationToken ct = default) =>
            Unreadable.TryGetValue(Encoding.UTF8.GetString(content), out var why)
                ? Task.FromException(new RecognitionRefusedException(RecognitionRefusal.BadFile, why))
                : Task.CompletedTask;

        public Task EnsureReadyAsync(string profileCode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref readyChecks);
            return NotReady is { } refusal ? Task.FromException(refusal) : Task.CompletedTask;
        }

        public Task<ModuleRecognitionResult> RecognizeAsync(
            string profileCode, byte[] content, string mimeType, CancellationToken ct = default)
        {
            Assert.Equal(CostsRecognitionProfiles.InvoiceCode, profileCode);
            Kinds[Encoding.UTF8.GetString(content)] = mimeType;
            return answers.TryGetValue(Encoding.UTF8.GetString(content), out var answer)
                ? answer()
                : Task.FromException<ModuleRecognitionResult>(new RecognitionRefusedException(
                    RecognitionRefusal.NoAnswer, "В сценарии теста для этого файла ответа нет."));
        }
    }
}
