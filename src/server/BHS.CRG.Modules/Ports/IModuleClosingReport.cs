namespace BHS.CRG.Modules.Ports;

/// <summary>О каком закрытии ядро спрашивает модуль.</summary>
/// <param name="ConstructionId">Стройка — закрывается её контур; <c>null</c> — компания целиком.</param>
/// <param name="From">Первый день, который закрытие закроет ВПЕРВЫЕ; <c>null</c> — у контура ещё ничего
/// не закрыто, и закрывается всё по <paramref name="Through" /> включительно.</param>
/// <param name="Through">Последний закрываемый день.</param>
/// <param name="ClosedAhead">Стройки, закрытые СВОИМ закрытием дальше компании, — и по какой день
/// включительно. Только у закрытия компании; у закрытия стройки пусто. ⚠️ Их документы за эти дни уже
/// заперты и в перечень не идут: отрезок «с <paramref name="From" /> по <paramref name="Through" />» для
/// них закрывается впервые не целиком. Спрашивайте <see cref="ClosesAnew" />, а не сравнивайте даты сами.</param>
public sealed record ModuleClosingScope(
    Guid? ConstructionId, DateOnly? From, DateOnly Through, IReadOnlyDictionary<Guid, DateOnly> ClosedAhead)
{
    /// <summary>Закроет ли это закрытие день документа ВПЕРВЫЕ.</summary>
    /// <param name="constructionId">Стройка, на которую лёг документ (его доля); <c>null</c> — не на стройку.</param>
    public bool ClosesAnew(Guid? constructionId, DateOnly day) =>
        (From is not { } from || day >= from) && day <= Through
        && !(constructionId is { } site && ClosedAhead.TryGetValue(site, out var closed) && day <= closed);
}

/// <summary>Чем считают документы строки: «счёт», «счёта», «счетов» — склоняет ядро.</summary>
public sealed record ModuleClosingUnit(string One, string Few, string Many);

/// <summary>Строка раздела: что именно и сколько.</summary>
/// <param name="Key">Ключ строки — постоянный: по нему экран сопоставляет «было» и «стало».</param>
/// <param name="Text">Что это, с заглавной буквы и без числа: «Оплачены в периоде, но не разнесены».</param>
/// <param name="Count">Сколько документов.</param>
/// <param name="Amount">На сколько; <c>null</c> — строка денег не несёт.</param>
/// <param name="AmountPermission">Право, открывающее сумму. ⚠️ Модуль отдаёт сумму ВСЕГДА и называет
/// право: диалог — экран ядра, и прячет сумму оно, по праву смотрящего. Спрячь её модуль сам — запись о
/// закрытии зависела бы от того, кто закрывает.</param>
/// <param name="Note">Пояснение под строкой; <c>null</c> — нет.</param>
public sealed record ModuleClosingLine(
    string Key, string Text, int Count, ModuleClosingUnit Unit, decimal? Amount = null,
    string? AmountPermission = null, string? Note = null);

/// <summary>Раздел модуля в диалоге закрытия периода.</summary>
/// <param name="DateRule">По какой дате модуль относит документ к периоду: «Счёт относится к периоду по
/// учётному периоду оплаты» (ТЗ CORE-35).</param>
/// <param name="Unfinished">Не завершено: закрытию не мешает, но после него останется как есть.</param>
/// <param name="Frozen">Что войдёт в закрытый период.</param>
public sealed record ModuleClosingSection(
    string DateRule, IReadOnlyList<ModuleClosingLine> Unfinished, IReadOnlyList<ModuleClosingLine> Frozen);

/// <summary>
/// Что модуль скажет диалогу закрытия периода (ТЗ CORE-35; задача E1b, issue #1099). Модуль
/// регистрирует реализацию в <c>RegisterServices</c> — обычной службой, по этому интерфейсу; модуль,
/// которому сказать нечего, не регистрирует ничего, и раздела у него нет.
///
/// <para>Зовут дважды: когда диалог открыт (предпросмотр) и когда период закрывают — второй раз ПОД
/// ЗАМКОМ закрытия, и ответ ложится в запись о закрытии. Поэтому ответ обязан зависеть только от
/// данных и вопроса, но не от того, кто спрашивает.</para>
///
/// <para>⚠️ <b>Замка записи не брать</b> (<c>OpenPeriodWrite</c>): во втором вызове ядро держит его
/// исключительным, и отчёт ждал бы собственный замок — повисло бы закрытие и вся запись в учёт.
/// Отчёт только читает.</para>
///
/// <para>⚠️ Отказ — исключением: ядро отказывает в закрытии. Пустой раздел вместо отказа выглядел бы
/// как «незавершённого нет».</para>
/// </summary>
public interface IModuleClosingReport
{
    /// <summary>Код модуля, чей это раздел. Один раздел на модуль.</summary>
    string Module { get; }

    Task<ModuleClosingSection> ReportAsync(ModuleClosingScope scope, CancellationToken ct = default);
}
