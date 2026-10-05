using BHS.CRG.Domain.Periods;

namespace BHS.CRG.Application.Periods;

/// <summary>
/// Закрытие учётного периода (ТЗ CORE-35; задача E1a, issue #1081) — служба ЯДРА.
///
/// <para>Ядра, а не модуля: закрытый период запирает записи всех модулей разом, и работать он обязан
/// при любом составе поставки, включая пустой. «Закрыто» у счетов и у выработки, посчитанное каждым
/// модулем по-своему, сводить было бы нечем.</para>
///
/// <para>Служба — единственный, кто пишет и читает таблицу закрытий: только здесь у записи есть
/// автор, время, проверка «подряд, без пропусков» и замок против одновременной записи в учёт.
/// Полноту стережёт перепись <c>PeriodClosureInventoryTests</c>.</para>
/// </summary>
public interface IPeriodClosures
{
    /// <summary>Границы на сейчас. Один запрос на всё: реестр спрашивает про сотни записей сразу.</summary>
    Task<PeriodLedger> LedgerAsync(CancellationToken ct = default);

    /// <summary>
    /// Стройки, которые можно закрыть, — все существующие. Только идентификаторы: названия открывает
    /// своё право, а границы периода читает любой вошедший.
    /// </summary>
    Task<IReadOnlyList<Guid>> ConstructionsAsync(CancellationToken ct = default);

    /// <summary>«Сегодня» по часам компании — то, с чем сверяется запрет закрывать будущее.</summary>
    Task<DateOnly> TodayAsync(CancellationToken ct = default);

    /// <summary>
    /// Что покажет диалог закрытия: перечень по модулям и отпечаток увиденного (задача E1b, issue
    /// #1099). Ничего не пишет и замка не берёт; отказывает теми же словами, что и закрытие, — диалог
    /// узнаёт о негодном периоде до кнопки.
    /// </summary>
    Task<ClosingPreview> PreviewAsync(PreviewClosing request, CancellationToken ct = default);

    /// <summary>Закрыть период. Отказы — см. <see cref="PeriodLedger.EnsureCanClose" />.</summary>
    Task<PeriodClosure> CloseAsync(ClosePeriod request, CancellationToken ct = default);

    /// <summary>Отменить ПОСЛЕДНЕЕ закрытие контура — ошибочное. Причина обязательна.</summary>
    Task<PeriodClosure> ReopenAsync(ReopenPeriod request, CancellationToken ct = default);

    /// <summary>Записи — свежие сверху: кто, когда и что закрыл или отменил.</summary>
    Task<IReadOnlyList<PeriodClosure>> HistoryAsync(int take, CancellationToken ct = default);

    /// <summary>Все записи — для резервной копии.</summary>
    Task<IReadOnlyList<PeriodClosure>> ExportAsync(CancellationToken ct = default);

    /// <summary>
    /// Записи из копии: дописываются только недостающие. Обновления нет и быть не может — запись
    /// неизменяема, и upsert поверх собственной истории экземпляра стёр бы её. Возвращает число
    /// добавленных.
    /// </summary>
    Task<int> ImportAsync(IReadOnlyList<PeriodClosure> records, CancellationToken ct = default);
}

/// <param name="Seen">
/// Граница контура, которую видел решающий (<c>null</c> — «не закрыто ничего»). Сдвинулась —
/// отказ: человек подтверждал закрытие, глядя на другое состояние (приём <c>ifMatch</c>, issue #1141).
/// </param>
/// <param name="ReportSeen">
/// Отпечаток перечня, который видел решающий (<see cref="ClosingPreview.Stamp" />). Перечень под замком
/// закрытия оказался другим — отказ. <c>null</c> — закрывает не человек через диалог, сверять не с чем:
/// перечень всё равно считается и ложится в запись. Адрес закрытия <c>null</c> не принимает.
/// </param>
public sealed record ClosePeriod(
    PeriodContour Contour, DateOnly From, DateOnly Through, DateOnly? Seen, string? Reason, string? ReportSeen = null);

/// <summary>О каком закрытии спрашивают предпросмотр.</summary>
public sealed record PreviewClosing(PeriodContour Contour, DateOnly From, DateOnly Through);

/// <summary>Перечень диалога и отпечаток, который закрытие обязано назвать.</summary>
public sealed record ClosingPreview(ClosingReport Report, string Stamp);

/// <summary>
/// Перечни модулей к закрытию периода — их собирает корень композиции: служба закрытия живёт в
/// инфраструктуре, а о контрактах модулей ей знать нельзя (см. <c>IModuleWorkRunner</c> — тот же приём).
/// </summary>
public interface IClosingReports
{
    /// <param name="from">Первый день, который закрытие закроет ВПЕРВЫЕ; <c>null</c> — у контура ещё
    /// ничего не закрыто, и закрывается всё по <paramref name="through" />.</param>
    /// <param name="closedAhead">Стройки, чьи дни по названную дату уже закрыты их собственным закрытием
    /// (<see cref="PeriodLedger.ClosedAheadOfCompany" />); у закрытия стройки — пусто.</param>
    /// <returns>Раздел на каждый включённый модуль, которому есть что сказать. Отказ модуля — отказ, а
    /// не пропущенный раздел: пропуск выглядел бы как «незавершённого нет».</returns>
    Task<ClosingReport> CollectAsync(
        PeriodContour contour, DateOnly? from, DateOnly through, IReadOnlyDictionary<Guid, DateOnly> closedAhead,
        CancellationToken ct = default);
}

/// <inheritdoc cref="ClosePeriod" />
public sealed record ReopenPeriod(PeriodContour Contour, DateOnly? Seen, string Reason);
