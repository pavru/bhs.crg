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
}
