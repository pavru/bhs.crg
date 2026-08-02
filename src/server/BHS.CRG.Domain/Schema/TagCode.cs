namespace BHS.CRG.Domain.Schema;

/// <summary>
/// Разбор записи тэга в схеме: <c>«код»</c> либо <c>«код:параметр»</c> (issue #583).
///
/// Параметр появился ради составного ключа идентичности: порядок его компонентов задаёт пользователь
/// (<c>identity:1</c>, <c>identity:2</c>), а не порядок полей в типе. Порядок полей существует ради
/// формы ввода, и связывать с ним ключи значило бы менять все ключи при перестановке полей местами.
///
/// Разделитель — двоеточие: в кодах тэгов его нет (там точки, см. <see cref="FunctionalTag"/>),
/// поэтому разбор однозначен и обратно совместим — запись без параметра остаётся собой.
/// </summary>
public readonly record struct TagCode(string Code, int? Order)
{
    /// <summary>Поле без номера идёт ПОСЛЕ нумерованных: существующие схемы работают без правки,
    /// а номера появляются только там, где порядок важен.</summary>
    public int SortKey => Order ?? int.MaxValue;

    public static TagCode Parse(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return new("", null);

        var sep = value.IndexOf(':');
        if (sep < 0) return new(value, null);

        var code = value[..sep].Trim();
        var param = value[(sep + 1)..].Trim();
        // Нечисловой или отрицательный параметр — не ошибка схемы, а просто «без номера»: тэг обязан
        // продолжать работать. Иначе опечатка в номере молча отключила бы поле от сопоставления.
        return int.TryParse(param, out var order) && order >= 0 ? new(code, order) : new(code, null);
    }

    /// <summary>Код тэга без параметра — то, с чем сравнивается функционал.</summary>
    public static string CodeOf(string? raw) => Parse(raw).Code;
}
