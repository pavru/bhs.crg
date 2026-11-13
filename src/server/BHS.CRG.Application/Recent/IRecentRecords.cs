namespace BHS.CRG.Application.Recent;

/// <summary>
/// Служба ядра «Недавние» (ТЗ CORE-25.3) — то, что о личных отметках открытия нужно коду вне адресов
/// <c>/api/account/recent</c>. Реализация — рядом с ними (<c>RecentRecords</c> в <c>BHS.CRG.Api</c>).
/// </summary>
public interface IRecentRecords
{
    /// <summary>
    /// Перенести отметки записи <paramref name="from" /> на <paramref name="to" /> того же типа — у всех
    /// пользователей; у кого отмечены обе, остаётся одна, с поздним открытием (shell-09, Р-5: удалённая
    /// версия шаблона не уносит живой шаблон из «Недавних»).
    ///
    /// <para>⚠️ Пишет в ТУ ЖЕ единицу работы, что вызывающий, и сама не сохраняет: перенос ложится в базу
    /// следующим <c>SaveChanges</c> вызывающего — вместе с удалением или не ложится вовсе.</para>
    /// </summary>
    Task ReassignAsync(string recordType, Guid from, Guid to, CancellationToken ct);
}

/// <summary>Коды типов записей модуля <c>id</c> — те же, что у его групп «Поиска записей».</summary>
public static class IdRecordTypes
{
    /// <summary>Комплект документов.</summary>
    public const string DocumentSet = "КомплектДокументов";

    /// <summary>Шаблон документа — все его версии одной записью (shell-09, Р-5).</summary>
    public const string Template = "ШаблонДокумента";
}
