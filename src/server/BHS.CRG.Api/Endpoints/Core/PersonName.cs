namespace BHS.CRG.Api.Endpoints.Core;

/// <summary>
/// Совпадение ФИО сотрудника с именем учётной записи — для предложения в режиме «Сопоставить»
/// (ТЗ WORK-47, выбор владельца по макету Р1: «предложение по ФИО даёт сервер»).
///
/// <para>Совпадают: одно и то же ФИО целиком и ФИО против «Фамилия И. О.». Без учёта регистра,
/// «ё» = «е», лишние пробелы и точки не в счёт. Фамилия обязана совпасть целиком, а число частей —
/// быть одним: «Иванов И.» не предлагается «Иванову Ивану Ивановичу» — у второго тёзки с другим
/// отчеством предложение было бы неверным, а человек принял бы его, не глядя.</para>
///
/// <para>Это подсказка, а не решение: строку с предложением человек отмечает сам, а две подходящие
/// учётные записи сервер не выбирает вовсе.</para>
/// </summary>
public static class PersonName
{
    public static bool Same(string? employee, string? account)
    {
        var a = Parts(employee);
        var b = Parts(account);
        if (a.Length < 2 || a.Length != b.Length || a[0] != b[0]) return false;
        for (var i = 1; i < a.Length; i++)
            if (!SamePart(a[i], b[i])) return false;
        return true;
    }

    private static bool SamePart(string x, string y) =>
        x == y
        || (x.Length == 1 && y.StartsWith(x, StringComparison.Ordinal))
        || (y.Length == 1 && x.StartsWith(y, StringComparison.Ordinal));

    private static string[] Parts(string? name) =>
        (name ?? "").ToLowerInvariant().Replace('ё', 'е')
            .Split([' ', '.', '\t', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
}
