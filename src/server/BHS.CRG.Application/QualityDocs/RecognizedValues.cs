using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.QualityDocs;

/// <summary>
/// Ответ движка → значения В ОБЪЯВЛЕННОМ ВИДЕ (issue #1005): «3» в числовом поле становится числом,
/// «да» в логическом — логическим, «01.02.2026» в поле даты — датой по ISO.
///
/// <para>Зачем это здесь, а не на клиенте: правило приведения у нас ОДНО (<see cref="ValueCoercion"/>),
/// и вторая его копия на TypeScript разошлась бы с первой на первом же частном случае — ровно так
/// уже расходились сканер выпуска и аудит (issue #642). Заодно приведение достаётся всем
/// потребителям адреса разом, включая безголовый путь импорта, где формы нет вовсе.</para>
///
/// <para>⚠️ Доккомментарий <see cref="ValueCoercion"/> гласит, что на пути ЗАПИСИ приведения нет
/// намеренно — иначе значение молча менялось бы в пяти местах и стало бы непонятно, кто его
/// переписал; распознавание названо там прямо. Противоречия нет: здесь значение не переписывается,
/// а РОЖДАЕТСЯ. Распознавание — его автор, человек видит результат в форме и подтверждает
/// сохранением. Молчаливой подмены чужого значения не происходит.</para>
///
/// <para>Чего НЕ делаем. Неразобравшееся значение остаётся строкой как есть, а не исчезает:
/// распознавание — единственное, что прочитало скан, и потерять прочитанное хуже, чем показать
/// расхождение (его покажет форма и найдёт аудит). Вариант перечисления не угадываем — подпись
/// отображает в код клиент, у которого есть реестр.</para>
///
/// <para>⚠️ Целочисленность примитива здесь не проверяется: клиент присылает уже РАЗРЕШЁННЫЙ базовый
/// тип поля («number»), а ограничения остаются при примитиве, которого на этой стороне нет. «3,5» в
/// целочисленном поле станет числом 3.5 — видом это верно, ограничением нет, и скажет об этом
/// аудит. Ошибка безопасная: вид значения — то, по чему читает код.</para>
/// </summary>
public static class RecognizedValues
{
    private static readonly Dictionary<Guid, PrimitiveType> NoPrimitives = [];

    /// <param name="values">Сырой ответ движка: путь поля → текст.</param>
    /// <param name="fields">Поля, которые просили распознать, — в них объявлен вид значения.</param>
    public static IReadOnlyDictionary<string, JsonNode?> InDeclaredShape(
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<RecognitionField> fields)
    {
        // Первое объявление пути выигрывает: список полей строит клиент обходом схемы, и повтор
        // пути там означал бы два поля с одним адресом — приводить по второму нечего.
        var declared = new Dictionary<string, RecognitionField>(StringComparer.Ordinal);
        foreach (var f in fields) declared.TryAdd(f.Path, f);

        var shaped = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (path, raw) in values)
        {
            JsonNode? node = raw is null ? null : JsonValue.Create(raw);
            if (raw is not null && declared.TryGetValue(path, out var field)
                && ValueCoercion.TryCoerce(
                    new SchemaFieldInfo(path, field.Type, null), node, NoPrimitives, out var coerced, out _))
                node = coerced;
            shaped[path] = node;
        }
        return shaped;
    }
}
