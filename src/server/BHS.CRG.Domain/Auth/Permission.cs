using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Auth;

/// <summary>
/// Право в базе — отражение того, что объявлено в коде (ТЗ AUTH-1).
///
/// Зачем хранить то, что и так есть в коде. Роль — набор прав, и хранится она в базе; значит,
/// на права нужно чем-то ссылаться, а редактору ролей — откуда-то брать список с объяснениями.
/// Источник истины при этом остаётся в коде: таблица наполняется при старте и правится только им.
///
/// ⚠️ Исчезнувшее из кода право НЕ удаляется, а помечается устаревшим. Удаление оборвало бы
/// ссылки из ролей, и администратор увидел бы роль, молча потерявшую часть состава; пометка же
/// оставляет след — «право было, его больше не выдают», — который видно в редакторе.
/// </summary>
public class Permission : Entity
{
    /// <summary>Код вида <c>модуль.объект.действие</c>. Уникален, по нему идут все ссылки.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Что право даёт — словами для администратора.</summary>
    public string Gives { get; private set; } = default!;

    /// <summary>К каким данным открывает доступ.</summary>
    public string Opens { get; private set; } = default!;

    /// <summary>С какими правами обычно выдаётся вместе: подсказка редактору ролей.</summary>
    public IReadOnlyList<string> UsuallyWith { get; private set; } = [];

    /// <summary>
    /// Право объявлено кодом этой сборки. Сброшенный признак означает «когда-то было, сейчас не
    /// выдаётся»: модуль выключили или право убрали из кода.
    /// </summary>
    public bool IsDeclared { get; private set; } = true;

    private Permission() { }

    public static Permission Declare(string code, string gives, string opens, IReadOnlyList<string> usuallyWith) =>
        new() { Code = code, Gives = gives, Opens = opens, UsuallyWith = usuallyWith };

    /// <summary>Обновляет объяснение и возвращает право в строй, если оно вернулось в код.</summary>
    public void Redeclare(string gives, string opens, IReadOnlyList<string> usuallyWith)
    {
        var changed = Gives != gives || Opens != opens || !UsuallyWith.SequenceEqual(usuallyWith) || !IsDeclared;
        if (!changed) return;

        Gives = gives;
        Opens = opens;
        UsuallyWith = usuallyWith;
        IsDeclared = true;
        TouchUpdatedAt();
    }

    /// <summary>Право исчезло из кода: не выдаём, но и не теряем.</summary>
    public void MarkUndeclared()
    {
        if (!IsDeclared) return;
        IsDeclared = false;
        TouchUpdatedAt();
    }
}
