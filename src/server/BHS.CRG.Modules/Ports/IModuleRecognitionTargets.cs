using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Modules.Ports;

/// <summary>
/// Куда ложатся поля профиля распознавания (issue #1483): цели, объявленные модулем
/// (<see cref="ModuleRecognitionProfile.Targets" />), плюс поля, которые заказчик дописал в его тип.
///
/// <para><b>Отдельным портом, а не полем ответа распознавания</b> — по двум причинам. Схема типа читается
/// базой, а распознавание идёт десятки секунд у движка: держать одно в другом незачем. И у кода, который
/// подменяет распознавание сценарием (тесты), сопоставление остаётся настоящим — по живой схеме типа.</para>
///
/// <para>⚠️ Сопоставление — точное совпадение строк, без регистра и «ё/е»: ключ пишет администратор, и
/// угадывание сделало бы опечатку тихой. Не легшее модуль называет в итоге распознавания.</para>
/// </summary>
public interface IModuleRecognitionTargets
{
    /// <param name="profileCode">Код заводского профиля модуля.</param>
    /// <param name="keys">Ключи полей, которые были в запросе (<see cref="ModuleRecognitionResult.AskedFields" />).</param>
    /// <returns>По каждому ключу — цель либо <c>null</c>: у записи такого поля нет. Профиль без целей
    /// (<see cref="ModuleRecognitionProfile.Targets" /> не объявлены) — <c>null</c> у всех.</returns>
    Task<IReadOnlyDictionary<string, ModuleFieldTarget?>> ResolveAsync(
        string profileCode, IReadOnlyCollection<string> keys, CancellationToken ct = default);
}
