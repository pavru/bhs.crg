using System.Text.Json;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Аргументы фоновой работы модуля: код операции и то, что передал сам модуль.
///
/// <para>Обёртка нужна потому, что вид задачи один на все модули, а исполнителей много: без кода
/// операции в аргументах фоновый цикл знал бы только «это работа модуля» и выбирать было бы не по
/// чему. Формат читает и пишет ровно этот файл — снаружи его не разбирают, поэтому он и не описан
/// в контрактах.</para>
/// </summary>
/// <param name="Operation">Код операции модуля.</param>
/// <param name="Body">Аргументы модуля как есть: строка, обычно JSON.</param>
internal sealed record ModuleWorkPayload(string Operation, string? Body)
{
    internal static string Wrap(string operation, string? body) =>
        JsonSerializer.Serialize(new ModuleWorkPayload(operation, body));

    /// <summary>
    /// Разбор аргументов задачи. Негодные — отказ, называющий задачу: аргументы складывает
    /// постановка, поэтому испорченные здесь означают либо чужую запись в таблице задач, либо
    /// расхождение версий приложения на перезапуске (задача встала в очередь прежней сборкой).
    /// </summary>
    internal static ModuleWorkPayload Unwrap(Guid jobId, string? payload)
    {
        ModuleWorkPayload? parsed = null;
        try
        {
            parsed = payload is null ? null : JsonSerializer.Deserialize<ModuleWorkPayload>(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Аргументы задачи {jobId} не разобрать: работа модуля обязана нести код операции.", ex);
        }

        return parsed is null || string.IsNullOrWhiteSpace(parsed.Operation)
            ? throw new InvalidOperationException(
                $"В аргументах задачи {jobId} нет кода операции — искать исполнителя не по чему.")
            : parsed;
    }

    /// <summary>
    /// Найти исполнителя или отказать, назвав известные операции.
    ///
    /// ⚠️ Двух обработчиков с одним кодом быть не может: постановка нашла бы двух исполнителей, и
    /// выбор первого означал бы, что операцию выполняет то, что раньше зарегистрировалось. Это отказ,
    /// а не предпочтение.
    /// </summary>
    internal static IModuleJobHandler EnsureHandled(string operation, IEnumerable<IModuleJobHandler> handlers)
    {
        var found = handlers
            .Where(h => string.Equals(h.Operation, operation, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (found.Count > 1)
            throw new InvalidOperationException(
                $"Операцию «{operation}» объявили несколько обработчиков: " +
                string.Join(", ", found.Select(h => h.GetType().FullName)) +
                ". Код операции обязан быть один на исполнителя.");

        if (found.Count == 0)
        {
            var known = handlers.Select(h => h.Operation).Order(StringComparer.Ordinal).ToList();
            throw new InvalidOperationException(
                $"Фоновую операцию «{operation}» выполнять некому: обработчика с таким кодом модуль не " +
                "регистрировал. Задача не поставлена — иначе она осталась бы в очереди навсегда. " +
                (known.Count > 0
                    ? "Известные операции: " + string.Join(", ", known) + "."
                    : "Ни один модуль не зарегистрировал ни одной операции."));
        }

        return found[0];
    }
}

/// <summary>
/// Связывает фоновую работу с обработчиком модуля (задача M2 этапа 2, issue #1069).
///
/// <para>Живёт в корне композиции, а не в инфраструктуре, по направлению ссылок: фоновый цикл на
/// контракты модулей сослаться не может — модулем считается любой проект, сославшийся на них.
/// Поэтому цикл знает только «есть кому выполнить», а кто именно — известно здесь, где и без того
/// перечислены модули.</para>
/// </summary>
public sealed class ModuleWorkRunner(IEnumerable<IModuleJobHandler> handlers) : IModuleWorkRunner
{
    public Task RunAsync(Guid jobId, Guid targetId, Guid userId, string? payload,
        Func<string, int, int, Task> report, CancellationToken ct)
    {
        var work = ModuleWorkPayload.Unwrap(jobId, payload);
        var handler = ModuleWorkPayload.EnsureHandled(work.Operation, handlers);

        return handler.RunAsync(new ModuleJobRun(jobId, targetId, userId, work.Body, report), ct);
    }
}
