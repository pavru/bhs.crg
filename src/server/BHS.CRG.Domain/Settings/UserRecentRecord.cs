namespace BHS.CRG.Domain.Settings;

/// <summary>
/// Запись, которую пользователь недавно открывал (ТЗ CORE-25.3, «Недавние» Главной; shell-08, В-3 — Б).
///
/// <para>Помнится ссылка — тип и идентификатор, — а не карточка: название, раздел и стройку при чтении
/// называет владелец типа в правах спрашивающего (контракт «Поиск записей»). Так удалённая запись и
/// запись, закрытая правами после открытия, из «Недавних» пропадают сами, без уборки по событиям.</para>
///
/// <para>Отметка личная, а не правка записи: потерянного обновления у неё нет — последнее открытие
/// всегда верно, — поэтому и версии строки у неё нет.</para>
/// </summary>
public class UserRecentRecord
{
    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private UserRecentRecord() { }

    /// <summary>Чья отметка. Вместе с типом и идентификатором — первичный ключ.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Код типа записи — тот же, что у группы поиска (<c>СчётНаОплату</c>, <c>Стройка</c>).</summary>
    public string RecordType { get; private set; } = "";

    public Guid RecordId { get; private set; }

    /// <summary>Когда открыта в последний раз: по нему порядок и вытеснение.</summary>
    public DateTimeOffset OpenedAt { get; private set; }

    /// <summary>
    /// Отметка той же записи под другим идентификатором — перенос (shell-09: удалённая версия шаблона
    /// передаёт отметку текущей). Ключ строки менять нельзя, поэтому перенос — новая строка вместо старой.
    /// </summary>
    public UserRecentRecord MovedTo(Guid recordId) =>
        new() { UserId = UserId, RecordType = RecordType, RecordId = recordId, OpenedAt = OpenedAt };

    /// <summary>Слить с другой отметкой той же записи: остаётся позднее открытие.</summary>
    public void Merge(UserRecentRecord other)
    {
        if (other.OpenedAt > OpenedAt) OpenedAt = other.OpenedAt;
    }
}
