using BHS.CRG.Application.Generation;
using Jint;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Jint-реализация <see cref="IExpressionEvaluator"/> (issue #368). Тот же песочный конфиг, что у
/// вычисляемых колонок наборов данных (<c>DataSetComputedColumnExecutor</c>): таймаут 1с + лимит
/// рекурсии. Значения полей доступны через <c>get("ключ")</c> — функция, а не переменные, потому что
/// ключи полей бывают кириллическими/невалидными JS-идентификаторами.
/// </summary>
public class JintExpressionEvaluator : IExpressionEvaluator
{
    public object? Evaluate(string expression, IReadOnlyDictionary<string, object?> variables)
    {
        var engine = new Engine(cfg => cfg
            .TimeoutInterval(TimeSpan.FromSeconds(1))
            .LimitRecursion(32));

        engine.SetValue("get", new Func<string, object?>(key =>
            variables.TryGetValue(key, out var v) ? v : null));

        var result = engine.Evaluate(expression);
        return result.IsNull() || result.IsUndefined() ? null : result.ToObject();
    }
}
