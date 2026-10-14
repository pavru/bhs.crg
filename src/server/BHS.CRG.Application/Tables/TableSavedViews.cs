namespace BHS.CRG.Application.Tables;

/// <summary>
/// Состояние экрана таблицы, которое хранит сохранённое представление (ТЗ CORE-33; задача G3a,
/// issue #1095). Те же пять вещей, что экран пишет в адрес; страницы, размера страницы и открытой
/// строки здесь нет — это не настройка, а место, где человек остановился.
/// </summary>
/// <param name="Columns">Колонки в порядке показа; null — все колонки таблицы в её порядке.</param>
/// <param name="Filter">Отбор — строка с JSON дерева условий, как в запросе строк; null — отбора нет.</param>
/// <param name="Pinned">Сколько первых колонок закреплено слева.</param>
public record TableViewState(
    IReadOnlyList<string>? Columns = null,
    string? Filter = null,
    IReadOnlyList<TableSortRequest>? Sort = null,
    IReadOnlyList<TableViewTotalDto>? Totals = null,
    int Pinned = 0);

/// <summary>Сохранённое представление в ответе списка.</summary>
/// <param name="Mine">Автор — спрашивающий.</param>
/// <param name="Author">Имя автора; null — учётной записи автора больше нет.</param>
/// <param name="CanEdit">Вправе ли спрашивающий править и удалять: своё личное — да, общее — с правом
/// на общие представления. Экран по нему решает, показывать ли «Сохранить»: действия, которое
/// выполнить нельзя, на экране нет.</param>
/// <param name="BasedOn">Код представления модуля, из которого сделано; null — из таблицы целиком.</param>
/// <param name="Version">Версия строки — её называет правка заголовком <c>If-Match</c>.</param>
/// <param name="Withheld">Почему отбор представления спрашивающему НЕ отдан: он стоит на колонке,
/// закрытой ему правом. Тогда <c>State.Filter</c> пуст — и это не «отбора нет». Значения условия
/// («поставщик из списка», «сумма больше порога») выдавали бы содержимое закрытой колонки; экран
/// по этому слову обязан сказать, что отбор автора скрыт, а не открыть представление как есть.
/// null — отбор отдан целиком либо его нет.</param>
public record SavedTableViewDto(
    Guid Id, string Title, bool Shared, bool Mine, string? Author, bool CanEdit, string? BasedOn,
    TableViewState State, string Version, string? Withheld = null);

/// <summary>Тело создания и правки представления. Правка заменяет представление целиком.</summary>
public record SaveTableView(string? Title, bool Shared, TableViewState? State, string? BasedOn = null);
