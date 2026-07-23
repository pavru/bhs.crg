namespace BHS.CRG.Application.Generation;

/// <summary>
/// Вычислитель выражений расчётных полей (issue #368). Абстрагирует движок (Jint) от Application:
/// обход схемы/топосорт/диагностика (<see cref="ComputedFieldResolver"/>) тестируются без движка.
/// Выражение читает значения полей через функцию <c>get("ключ")</c>. Реализация обязана быть
/// песочницей (таймаут, лимит рекурсии) — недоверенный ввод админа.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Вычисляет <paramref name="expression"/>; <c>get(key)</c> возвращает <paramref name="variables"/>[key]
    /// (или null). Бросает при ошибке выражения/таймауте — вызывающий переводит это в диагностику.</summary>
    object? Evaluate(string expression, IReadOnlyDictionary<string, object?> variables);
}
