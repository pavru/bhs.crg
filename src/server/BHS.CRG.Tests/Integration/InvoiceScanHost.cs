using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Activity;
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
    /// Отказ справочника на вопрос «что это за записи» (<see cref="IModuleCatalog.RefsAsync" />) — пока
    /// стоит. Так тест проверяет, что необязательный вопрос о запомненном не губит распознавание. Тест
    /// ставит и обязан снять: хост у класса общий.
    /// </summary>
    public Exception? RefsFailure { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleRecognition>();
            services.AddSingleton<IModuleRecognition>(Recognition);

            services.RemoveAll<IModuleWriteGuard>();
            services.AddScoped<ModuleWriteGuardPort>();
            services.AddScoped<IModuleWriteGuard>(sp => new InterruptedGuard(sp.GetRequiredService<ModuleWriteGuardPort>(), this));

            services.RemoveAll<IModuleCatalog>();
            services.AddScoped<ModuleCatalogPort>();
            services.AddScoped<IModuleCatalog>(sp => new FailingCatalog(sp.GetRequiredService<ModuleCatalogPort>(), this));
        });
    }

    /// <summary>Настоящий справочник — с отказом на вопрос о записях по просьбе теста.</summary>
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
            host.RefsFailure is { } failure && entityType == CostsRecordTypes.NomenclatureCode
                ? Task.FromException<IReadOnlyList<ModuleCatalogRef>?>(failure)
                : inner.RefsAsync(entityType, ids, ct);

        public Task<ModuleCatalogFieldValues?> FieldValuesAsync(
            string entityType, string fieldKey, RecordsFor purpose, CancellationToken ct = default) =>
            inner.FieldValuesAsync(entityType, fieldKey, purpose, ct);
    }

    /// <summary>
    /// Записи журнала об этом счёте — прочитанные ПОСЛЕ конца фоновой задачи.
    ///
    /// <para>Журнал пишется после исхода и вне его: форма видит «done» на мгновение раньше записи. Чтение
    /// сразу за исходом через раз давало ноль там, где ждали запись, — и, что хуже, давало бы ноль там,
    /// где ждали НОЛЬ, при любом поведении (ревью PR #1263). Конец задачи — знак, после которого записи
    /// уже не появится: журнал она пишет последним.</para>
    /// </summary>
    public async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> JournalAsync(
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

        public Task EnsureReadyAsync(string profileCode, CancellationToken ct = default) =>
            NotReady is { } refusal ? Task.FromException(refusal) : Task.CompletedTask;

        public Task<ModuleRecognitionResult> RecognizeAsync(
            string profileCode, byte[] content, string mimeType, CancellationToken ct = default)
        {
            Assert.Equal(CostsRecognitionProfiles.InvoiceCode, profileCode);
            return answers.TryGetValue(Encoding.UTF8.GetString(content), out var answer)
                ? answer()
                : Task.FromException<ModuleRecognitionResult>(new RecognitionRefusedException(
                    RecognitionRefusal.NoAnswer, "В сценарии теста для этого файла ответа нет."));
        }
    }
}
