using BHS.CRG.Application.Generation;
using BHS.CRG.Infrastructure.Scripting;
using Jint;   // расширения JsValue: IsNull/IsUndefined

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Jint-реализация <see cref="IExpressionEvaluator"/> (issue #368). Ограничения песочницы — общие с
/// вычисляемыми колонками наборов данных, см. <see cref="JintSandbox"/>. Значения полей доступны
/// через <c>get("ключ")</c> — функция, а не переменные, потому что ключи полей бывают
/// кириллическими/невалидными JS-идентификаторами.
/// </summary>
public class JintExpressionEvaluator : IExpressionEvaluator
{
    public object? Evaluate(string expression, IReadOnlyDictionary<string, object?> variables)
    {
        var engine = JintSandbox.Create();

        engine.SetValue("get", new Func<string, object?>(key =>
            variables.TryGetValue(key, out var v) ? v : null));

        var result = engine.Evaluate(expression);
        return result.IsNull() || result.IsUndefined() ? null : result.ToObject();
    }
}
