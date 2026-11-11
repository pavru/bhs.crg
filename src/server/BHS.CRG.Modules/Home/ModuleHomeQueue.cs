namespace BHS.CRG.Modules.Home;

/// <summary>
/// Очередь Главной («Ждут вас», shell-08, ТЗ UI-34): что модуль ждёт от человека и сколько этого.
/// Объявление — в манифесте (<see cref="IAppModule.HomeQueues" />), число — контрактом
/// <see cref="IHomeQueueCounts" />. Близнец готового отбора таблицы (<c>ModuleTableShortcut</c>):
/// код, подпись, пояснение, ключи «не предлагается без них», тихая очередь.
///
/// <para><b>Объявление отдельно от числа нарочно.</b> По объявлению ядро решает, кому очередь
/// предлагать, ДО вызова модуля: право здесь — объявленные данные, а не суждение поставщика, и
/// поставщику, забывшему проверку, отдать лишнее нечем. А перечень всех очередей сборки сверяется с
/// клиентом (файл-перепись <c>homeQueues.json</c>): у каждой очереди обязан быть экран, куда ведёт
/// карточка, иначе карточка — упрёк без выхода.</para>
/// </summary>
/// <param name="Code">Код очереди внутри модуля: <c>parsing</c>. Пара «модуль, код» — адрес карточки
/// на клиенте.</param>
/// <param name="Title">Подпись карточки: «Разобрать». Та же, что у чипа экрана очереди.</param>
/// <param name="Hint">Что это и что делать — строкой под числом. Коротко: на телефоне карточки идут в
/// две колонки (<see cref="HintLimit" />).</param>
/// <param name="Requires">Права, ВСЕ из которых нужны, чтобы очередь предлагалась. Не «любое из»: право
/// на правку без права на чтение возможно (<c>UsuallyWith</c> — подсказка, а не зависимость), и
/// карточка вела бы на закрытый экран. Ключ модуля ядро проверяет само.</param>
/// <param name="Quiet">Очередь не зовёт к срочному действию («В архиве»): карточка тише соседних.</param>
public sealed record ModuleHomeQueue(
    string Code,
    string Title,
    string Hint,
    IReadOnlyList<string> Requires,
    bool Quiet = false)
{
    /// <summary>Сколько знаков пояснения помещается в карточку на телефоне (две колонки по ~170 px).</summary>
    public const int HintLimit = 50;

    /// <summary>Что не так с объявлением; пусто — годно. Собирает всё, а не первое.</summary>
    /// <param name="permissions">Права сборки — модулей (и выключенных) и ядра.</param>
    public IReadOnlyList<string> Problems(IReadOnlySet<string> permissions)
    {
        var problems = new List<string>();
        var name = string.IsNullOrWhiteSpace(Code) ? "без кода" : $"«{Code}»";
        void Add(string problem) => problems.Add($"очередь Главной {name}: {problem}");

        if (string.IsNullOrWhiteSpace(Code)) Add("не назван код");
        if (string.IsNullOrWhiteSpace(Title)) Add("нет подписи для человека");
        if (string.IsNullOrWhiteSpace(Hint)) Add("нет пояснения");
        else if (Hint.Length > HintLimit) Add($"пояснение длиннее {HintLimit} знаков — на телефоне не поместится");

        // Без права очередь предлагалась бы каждому, кому открыт модуль, — и бухгалтеру с одними
        // накладными тоже. Опечатка в коде права не предлагала бы очередь НИКОМУ, молча.
        if (Requires is not { Count: > 0 }) Add("не названо ни одного права");
        else
            foreach (var code in Requires.Where(c => string.IsNullOrWhiteSpace(c) || !permissions.Contains(c)))
                Add($"права «{code}» в сборке нет");

        return problems;
    }
}

/// <summary>Число очереди, как его отдаёт модуль.</summary>
/// <param name="Code">Код очереди из объявления.</param>
/// <param name="Count">Сколько; <c>null</c> — не посчитано (тогда назван <paramref name="Refused" />).</param>
/// <param name="Doubt">Почему числу нельзя верить как полному. ⚠️ Ноль с этой причиной — не «пусто».</param>
/// <param name="Refused">Почему числа нет — словами для человека.</param>
public sealed record HomeQueueCount(string Code, int? Count, string? Doubt = null, string? Refused = null);
