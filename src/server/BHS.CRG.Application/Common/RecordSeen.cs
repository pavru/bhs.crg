using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Сверка версии записи общих данных, названной правкой, с лежащей в базе (issue #1214).
///
/// <para>Правка записи заменяет её ЦЕЛИКОМ — название, данные, псевдонимы. Из двух форм, открытых
/// одновременно, побеждала сохранённая последней, и правка первой пропадала без сообщения. Тот же
/// класс, что у источника набора данных (#1141) и у счёта (#1176): правка обязана назвать версию,
/// по которой собрана.</para>
/// </summary>
public static class RecordSeen
{
    /// <summary>Текст отказа — один у обеих сверок: ранней и той, что под блокировкой.</summary>
    public const string Moved =
        "Запись тем временем изменили, и эта правка не сохранена — сохранённая поверх, она затёрла бы " +
        "чужую. Откройте запись заново и внесите правку ещё раз.";

    public static void Ensure(string stored, string seen)
    {
        if (!string.Equals(stored, seen.Trim(), StringComparison.Ordinal))
            throw new ConflictException(Moved);
    }
}
