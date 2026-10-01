using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Tables;

/// <content>Колонка-перечень: условие по дочернему зерну.</content>
public sealed partial class TableSql<T>
{
    /// <summary>
    /// Колонка-перечень — поле нижнего зерна, свёрнутое в строку верхнего: объекты, на которые разнесён
    /// счёт (ТЗ CORE-33; задача G1c, issue #1090). Условие по ней — условие ПО ДОЧЕРНЕМУ ЗЕРНУ, в базе
    /// это <c>EXISTS</c>: «есть часть, у которой объект — X».
    ///
    /// <para>Как у справочника, в базе лежат ссылки, а человек отбирает по названию: названия сверяет
    /// правило ядра, в запрос уходят подошедшие ссылки.</para>
    /// </summary>
    /// <param name="keys">Ссылки дочерних строк — подзапросом от строки таблицы.</param>
    /// <param name="labels">Названия по ссылкам.</param>
    /// <param name="lost">Как названа ссылка, которой в <paramref name="labels" /> нет: «объект удалён».
    /// ⚠️ Потерянная ссылка — НЕ пустое место, в отличие от справочника: у счёта, разнесённого на
    /// удалённую стройку, разноска есть, и «объект пуст» на нём было бы ложью — он попал бы в «не
    /// разнесённые».</param>
    public TableSql<T> List<TKey>(
        string key, Expression<Func<T, IEnumerable<TKey?>>> keys, IReadOnlyDictionary<TKey, string> labels,
        string lost)
        where TKey : struct => Add(key, new ListColumn<TKey>(keys, labels, lost));

    private sealed class ListColumn<TKey>(
        Expression<Func<T, IEnumerable<TKey?>>> keys, IReadOnlyDictionary<TKey, string> labels, string lost) : Column
        where TKey : struct
    {
        private readonly TKey?[] _known = [.. labels.Keys.Select(k => (TKey?)k)];

        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.List;

        private Expression<Func<T, bool>> NotEmpty => Compose(keys, all => all.Any());

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            // «Пусто» — дочерних строк нет вовсе; правило ядра на пустой клетке отвечает, о каком из
            // двух вопросов речь.
            if (TableFilters.IsPresence(condition.Op))
                return condition.Matches(null) ? Not(NotEmpty) : NotEmpty;

            // Название сверяет правило ядра — то же, каким исполнитель в памяти сверяет клетку. Одно
            // название — перечень из одного элемента, поэтому ответ правила на нём и есть ответ
            // «подходит ли элемент»; у отрицания он перевёрнут.
            var negative = TableFilters.IsNegative(condition.Op);
            bool Fits(string label) => condition.Matches(label) != negative;

            var fitting = labels.Where(l => Fits(l.Value)).Select(l => (TKey?)l.Key).ToArray();
            var found = Compose(keys, all => all.Any(key => fitting.Contains(key)));
            if (Fits(lost)) found = Or(found, Compose(keys, all => all.Any(key => !_known.Contains(key))));

            // Отрицание — «нет НИ ОДНОЙ такой», а не «есть хоть одна другая»: счёт, разнесённый на X и
            // на Y, под «объект не X» не попадает.
            return negative ? Not(found) : found;
        }

        /// <summary>
        /// По первому названию перечня, пустые — в конце. Потерянные ссылки идут первыми: о счёте,
        /// разнесённом на удалённую стройку, лучше узнать раньше.
        /// </summary>
        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first)
        {
            var ordered = labels
                .OrderBy(l => l.Value, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true))
                .Select(l => (TKey?)l.Key).ToArray();
            return By(By(rows, Not(NotEmpty), false, first),
                Compose(keys, all => all.Min(key => (int?)Array.IndexOf(ordered, key))), descending, false);
        }

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(NotEmpty, ct), 0);
    }
}
