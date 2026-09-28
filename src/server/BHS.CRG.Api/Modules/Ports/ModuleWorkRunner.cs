using System.Text.Json;
using BHS.CRG.Application.Jobs;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Аргументы фоновой работы модуля: код операции и то, что передал сам модуль, — плюс правила о коде
/// операции, общие для постановки и выполнения.
///
/// <para>Обёртка нужна потому, что вид задачи один на все модули, а исполнителей много: без кода
/// операции в аргументах фоновый цикл знал бы только «это работа модуля» и выбирать было бы не по
/// чему. Формат читает и пишет ровно этот файл — снаружи его не разбирают, поэтому он и не описан
/// в контрактах.</para>
/// </summary>
/// <param name="Operation">Код операции модуля.</param>
/// <param name="Body">Аргументы модуля как есть: строка, обычно JSON.</param>
internal sealed record ModuleWork(string Operation, string? Body)
{
    internal static string Wrap(string operation, string? body) =>
        JsonSerializer.Serialize(new ModuleWork(operation, body));

    /// <summary>
    /// Разбор аргументов задачи. Негодные — отказ, называющий задачу: аргументы складывает
    /// постановка, поэтому испорченные здесь означают либо чужую запись в таблице задач, либо
    /// расхождение версий приложения на перезапуске (задача встала в очередь прежней сборкой).
    /// </summary>
    internal static ModuleWork Unwrap(Guid jobId, string? payload)
    {
        ModuleWork? parsed = null;
        try
        {
            parsed = payload is null ? null : JsonSerializer.Deserialize<ModuleWork>(payload);
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
    /// Код операции обязан начинаться с кода включённого модуля — то же правило, что у действий
    /// журнала.
    ///
    /// <para>Зачем. Операции ищутся по коду среди ВСЕХ модулей: без префикса два модуля, назвавшие
    /// свою операцию «import», столкнулись бы — и столкновение вылезло бы у обоих сразу, отказом при
    /// постановке, в котором не видно, чья операция лишняя. С префиксом столкновение невозможно по
    /// построению.</para>
    ///
    /// <para>⚠️ Чего проверка НЕ делает, сказано вслух: она не доказывает, что операцию ставит тот
    /// самый модуль. Кто вызвал порт, контейнеру неизвестно — ни здесь, ни в журнале, — и модули
    /// живут в одном процессе, так что барьера между ними нет вовсе. Проверка убирает столкновение
    /// имён, а не изоляцию модулей друг от друга: изоляции в системе нет и не обещано.</para>
    /// </summary>
    internal static void EnsureNamedByModule(string operation, ModuleRegistry modules)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new InvalidOperationException("У фоновой операции модуля нет кода: искать исполнителя не по чему.");

        var module = operation.Split('.')[0];
        if (module.Length == operation.Length || !modules.IsEnabled(module))
            throw new InvalidOperationException(
                $"Код фоновой операции «{operation}» обязан начинаться с кода модуля: «{module}» — " +
                $"не модуль этого экземпляра. Включены: {string.Join(", ", modules.Codes)}. Без " +
                "префикса два модуля, назвавшие операцию одинаково, столкнулись бы — и разбираться " +
                "в этом пришлось бы обоим.");
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
        var all = handlers.ToList();
        var found = all
            .Where(h => string.Equals(h.Operation, operation, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (found.Count > 1)
            throw new InvalidOperationException(
                $"Операцию «{operation}» объявили несколько обработчиков: " +
                string.Join(", ", found.Select(h => h.GetType().FullName)) +
                ". Код операции обязан быть один на исполнителя.");

        if (found.Count == 0)
        {
            var known = all.Select(h => h.Operation).Order(StringComparer.Ordinal).ToList();
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
        var work = ModuleWork.Unwrap(jobId, payload);
        var handler = ModuleWork.EnsureHandled(work.Operation, handlers);

        return handler.RunAsync(new ModuleJobRun(jobId, targetId, userId, work.Body, report), ct);
    }
}
