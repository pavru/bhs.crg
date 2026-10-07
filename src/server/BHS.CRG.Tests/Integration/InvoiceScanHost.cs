using System.Collections.Concurrent;
using System.Text;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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
        });
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
