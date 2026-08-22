namespace BHS.CRG.Application.Updates;

/// <summary>
/// Что система знает о версиях: своей и выпущенной (issue #813).
///
/// <paramref name="LastCheckedAt"/> отдаётся не для красоты: без него «обновлений нет» неотличимо
/// от «уже сутки не можем достучаться до GitHub», и второе выглядело бы как первое.
/// </summary>
/// <param name="Installed">Версия работающей сборки.</param>
/// <param name="Latest">Последняя выпущенная версия; null — ещё ни разу не узнали.</param>
/// <param name="UpdateAvailable">Выпущенная строго новее установленной.</param>
/// <param name="ReleaseUrl">Страница выпуска с заметками — человеку, а не машине.</param>
/// <param name="ReleaseNotes">Заметки релиза (тело GitHub Release), если известны.</param>
/// <param name="LastCheckedAt">Когда проверка последний раз УДАЛАСЬ (не когда запускалась).</param>
/// <param name="Enabled">Включена ли проверка. Выключена — в сеть не ходим вовсе.</param>
public record UpdateStatus(
    string Installed,
    string? Latest,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset? LastCheckedAt,
    bool Enabled);

public interface IUpdateCheck
{
    /// <summary>Текущее знание о версиях — из состояния службы, БЕЗ похода в сеть.</summary>
    Task<UpdateStatus> GetStatusAsync(CancellationToken ct = default);
}
