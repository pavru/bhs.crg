using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Tables;

/// <content>Колонки запроса: как условие, сортировка и итог ложатся на каждый вид значения.</content>
public sealed partial class TableSql<T>
{
    /// <summary>Число из текста клетки поля схемы; не число — null, и под сравнение клетка не попадает.</summary>
    private static readonly Expression<Func<string?, decimal?>> ToNumber =
        s => s != null && Regex.IsMatch(s, TableSqlRules.NumberPattern) ? (decimal?)Convert.ToDecimal(s) : null;

    /// <summary>Дата ISO из текста клетки — первые десять знаков; не дата — null.</summary>
    private static readonly Expression<Func<string?, string?>> ToIsoDate =
        s => s != null && Regex.IsMatch(s, TableSqlRules.DatePattern) ? s.Substring(0, 10) : null;

    private static readonly Expression<Func<string?, bool>> TextIsEmpty = s => s == null || s == "";

    private abstract class Column
    {
        public abstract ModuleTableColumnKind Kind { get; }
        public abstract Expression<Func<T, bool>> Test(TableFilterCondition condition);
        public abstract IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first);
        public abstract Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct);

        /// <summary>
        /// Операторы, общие всем видам: «пусто», равенство и перечень. Отрицания — именно «не подошло
        /// под положительное», поэтому «не равно» включает пустые клетки. null — оператор не из общих,
        /// его переводит сама колонка.
        /// </summary>
        protected static Expression<Func<T, bool>>? Common(
            TableFilterCondition condition, Expression<Func<T, bool>> isEmpty,
            Func<string, Expression<Func<T, bool>>> equalTo) => condition.Op switch
        {
            "is_empty" or "is_null" => isEmpty,
            "is_not_empty" or "is_not_null" => Not(isEmpty),
            "eq" => equalTo(condition.Values[0]),
            "neq" => Not(equalTo(condition.Values[0])),
            "in" => condition.Values.Select(equalTo).Aggregate(Or),
            "not_in" => Not(condition.Values.Select(equalTo).Aggregate(Or)),
            _ => null,
        };

        protected static InvalidOperationException Unknown(TableFilterCondition condition) =>
            new($"Оператора «{condition.Op}» у колонки «{condition.Column}» запрос к базе не умеет.");
    }

    /// <summary>Текст: регистр не различается, пустая клетка — пустая строка.</summary>
    private sealed class TextColumn(Expression<Func<T, string?>> value) : Column
    {
        private readonly Expression<Func<T, string>> _upper = Compose(value, s => (s ?? "").ToUpper());

        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Text;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            if (Common(condition, Compose(value, TextIsEmpty), v => With(v, (s, text) => s == text)) is { } common)
                return common;

            var value0 = condition.Values[0];
            return condition.Op switch
            {
                "contains" => With(value0, (s, text) => s.Contains(text)),
                "not_contains" => Not(With(value0, (s, text) => s.Contains(text))),
                "starts_with" => With(value0, (s, text) => s.StartsWith(text)),
                "ends_with" => With(value0, (s, text) => s.EndsWith(text)),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, TextIsEmpty), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(Not(Compose(value, TextIsEmpty)), ct), 0);

        /// <summary>Правило над клеткой без регистра и значением условия без регистра.</summary>
        private Expression<Func<T, bool>> With(string text, Expression<Func<string, string, bool>> rule)
        {
            var upper = text.ToUpperInvariant();
            return Compose(_upper, Bind(rule, upper));
        }
    }

    /// <summary>
    /// Число. <paramref name="raw" /> — текст клетки, если колонка — поле схемы: там бывает и не
    /// число, и итог обязан назвать, сколько таких («не учтено N значений»).
    /// </summary>
    private sealed class NumberColumn(Expression<Func<T, decimal?>> value, Expression<Func<T, string?>>? raw) : Column
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Number;

        private Expression<Func<T, bool>> IsEmpty =>
            raw is null ? Compose(value, n => n == null) : Compose(raw, TextIsEmpty);

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            static decimal Parse(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

            if (Common(condition, IsEmpty, v => With(Parse(v), (n, x) => n == x)) is { } common) return common;

            var a = Parse(condition.Values[0]);
            return condition.Op switch
            {
                "gt" => With(a, (n, x) => n > x),
                "gte" => With(a, (n, x) => n >= x),
                "lt" => With(a, (n, x) => n < x),
                "lte" => With(a, (n, x) => n <= x),
                "between" => And(With(a, (n, x) => n >= x), With(Parse(condition.Values[1]), (n, x) => n <= x)),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, n => n == null), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            // Один запрос на колонку. «Не учтено» — не число и не пусто; у настоящей числовой колонки
            // текста нет, и таких не бывает.
            var total = await rows.Select(Pair(value, raw)).GroupBy(_ => 1).Select(g => new
            {
                Count = g.LongCount(c => c.Value != null),
                Skipped = g.LongCount(c => c.Value == null && c.Raw != null && c.Raw != ""),
                Sum = g.Sum(c => c.Value),
                Min = g.Min(c => c.Value),
                Max = g.Max(c => c.Value),
            }).FirstOrDefaultAsync(ct);

            return total is null ? new(0, 0)
                : total.Count == 0 ? new(0, total.Skipped)
                : new(total.Count, total.Skipped, total.Sum, total.Min, total.Max);
        }

        private Expression<Func<T, bool>> With(decimal number, Expression<Func<decimal?, decimal, bool>> rule) =>
            Compose(value, Bind(rule, number));
    }

    /// <summary>Дата в колонке базы.</summary>
    private sealed class DateColumn(Expression<Func<T, DateOnly?>> value) : Column
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Date;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            static DateOnly Parse(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (Common(condition, Compose(value, d => d == null), v => With(Parse(v), (d, x) => d == x)) is { } common)
                return common;

            var a = Parse(condition.Values[0]);
            return condition.Op switch
            {
                "gt" => With(a, (d, x) => d > x),
                "gte" => With(a, (d, x) => d >= x),
                "lt" => With(a, (d, x) => d < x),
                "lte" => With(a, (d, x) => d <= x),
                "between" => And(With(a, (d, x) => d >= x), With(Parse(condition.Values[1]), (d, x) => d <= x)),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, d => d == null), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            var (count, skipped, min, max) = await RangeAsync(rows, value, null, ct);
            return count == 0 ? new(0, skipped) : new(count, skipped, Min: min, Max: max);
        }

        private Expression<Func<T, bool>> With(DateOnly date, Expression<Func<DateOnly?, DateOnly, bool>> rule) =>
            Compose(value, Bind(rule, date));
    }

    /// <summary>
    /// Дата в поле схемы — строка ISO. Сравнивается первыми десятью знаками: у строк одной формы
    /// порядок знаков и есть порядок дат. Клетка, где лежит не дата, под сравнение не попадает.
    /// </summary>
    private sealed class IsoDateColumn(Expression<Func<T, string?>> raw) : Column
    {
        private readonly Expression<Func<T, string?>> _date = Compose(raw, ToIsoDate);

        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Date;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            if (Common(condition, Compose(raw, TextIsEmpty), v => With(v, (d, x) => d == x)) is { } common)
                return common;

            var a = condition.Values[0];
            return condition.Op switch
            {
                "gt" => With(a, (d, x) => string.Compare(d, x) > 0),
                "gte" => With(a, (d, x) => string.Compare(d, x) >= 0),
                "lt" => With(a, (d, x) => string.Compare(d, x) < 0),
                "lte" => With(a, (d, x) => string.Compare(d, x) <= 0),
                "between" => And(
                    With(a, (d, x) => string.Compare(d, x) >= 0),
                    With(condition.Values[1], (d, x) => string.Compare(d, x) <= 0)),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(_date, d => d == null), false, first), _date, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            var (count, skipped, min, max) = await RangeAsync(rows, _date, raw, ct);
            return count == 0 ? new(0, skipped) : new(count, skipped, Min: min, Max: max);
        }

        private Expression<Func<T, bool>> With(string date, Expression<Func<string?, string, bool>> rule) =>
            Compose(_date, Bind(rule, date));
    }

    /// <summary>Флаг. Значение условия — «true» или «false».</summary>
    private sealed class FlagColumn(Expression<Func<T, bool?>> value) : Column
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Boolean;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition) =>
            Common(condition, Compose(value, b => b == null),
                v => Compose(value, Bind<bool?, bool, bool>((b, x) => b == x, v == "true")))
            ?? throw Unknown(condition);

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, b => b == null), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(Compose(value, b => b != null), ct), 0);
    }

    /// <summary>Справочник: ссылка в базе, название у человека.</summary>
    private sealed class LookupColumn<TKey>(
        Expression<Func<T, TKey?>> id, IReadOnlyDictionary<TKey, string> labels) : Column
        where TKey : struct
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Text;

        private Expression<Func<T, bool>> Known
        {
            get
            {
                var known = labels.Where(l => l.Value.Length > 0).Select(l => (TKey?)l.Key).ToArray();
                return Compose(id, key => known.Contains(key));
            }
        }

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            // Названия сверяет правило ядра — то же, каким исполнитель в памяти сверяет клетку.
            var matching = labels.Where(l => condition.Matches(l.Value)).Select(l => (TKey?)l.Key).ToArray();
            var found = Compose(id, key => matching.Contains(key));

            // Пустая клетка — и «ссылки нет», и «ссылка ведёт в никуда»: на экране обе пусты.
            return condition.Matches(null) ? Or(found, Not(Known)) : found;
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first)
        {
            var ordered = labels.Where(l => l.Value.Length > 0)
                .OrderBy(l => l.Value, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true))
                .Select(l => (TKey?)l.Key).ToArray();
            return By(By(rows, Not(Known), false, first), Compose(id, key => Array.IndexOf(ordered, key)), descending, false);
        }

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(Known, ct), 0);
    }

    // ── Итоги ───────────────────────────────────────────────────────────────────

    /// <summary>Значение колонки рядом с текстом клетки — чтобы итог и «не учтено» считал один запрос.</summary>
    private sealed class Cell<TValue>
    {
        public TValue Value { get; init; } = default!;
        public string? Raw { get; init; }
    }

    private static Expression<Func<T, Cell<TValue>>> Pair<TValue>(
        Expression<Func<T, TValue>> value, Expression<Func<T, string?>>? raw)
    {
        var row = value.Parameters[0];
        Expression text = raw is null
            ? Expression.Constant(null, typeof(string))
            : new Swap(raw.Parameters[0], row).Visit(raw.Body);
        return Expression.Lambda<Func<T, Cell<TValue>>>(
            Expression.MemberInit(
                Expression.New(typeof(Cell<TValue>)),
                Expression.Bind(typeof(Cell<TValue>).GetProperty(nameof(Cell<TValue>.Value))!, value.Body),
                Expression.Bind(typeof(Cell<TValue>).GetProperty(nameof(Cell<TValue>.Raw))!, text)),
            row);
    }

    /// <summary>Количество, «не учтено», минимум и максимум — одним запросом; для дат в обоих хранениях.</summary>
    private static async Task<(long Count, long Skipped, object? Min, object? Max)> RangeAsync<TValue>(
        IQueryable<T> rows, Expression<Func<T, TValue>> value, Expression<Func<T, string?>>? raw, CancellationToken ct)
    {
        var range = await rows.Select(Pair(value, raw)).GroupBy(_ => 1).Select(g => new
        {
            Count = g.LongCount(c => c.Value != null),
            Skipped = g.LongCount(c => c.Value == null && c.Raw != null && c.Raw != ""),
            Min = g.Min(c => c.Value),
            Max = g.Max(c => c.Value),
        }).FirstOrDefaultAsync(ct);

        return range is null ? (0, 0, null, null) : (range.Count, range.Skipped, range.Min, range.Max);
    }

    // ── Сборка выражений ────────────────────────────────────────────────────────

    /// <summary>Подставить значение колонки в правило: <c>row => rule(column(row))</c>.</summary>
    private static Expression<Func<T, TOut>> Compose<TIn, TOut>(
        Expression<Func<T, TIn>> column, Expression<Func<TIn, TOut>> rule) =>
        Expression.Lambda<Func<T, TOut>>(
            new Swap(rule.Parameters[0], column.Body).Visit(rule.Body), column.Parameters);

    /// <summary>
    /// Правило от клетки и значения условия → правило от клетки. Значение уходит в запрос ПАРАМЕТРОМ
    /// (обращением к полю объекта), а не текстом: иначе на каждое новое значение отбора у провайдера
    /// копился бы свой разобранный запрос.
    /// </summary>
    private static Expression<Func<TIn, TOut>> Bind<TIn, TArg, TOut>(Expression<Func<TIn, TArg, TOut>> rule, TArg argument)
    {
        var held = Expression.Property(Expression.Constant(new Held<TArg>(argument)), nameof(Held<TArg>.Value));
        return Expression.Lambda<Func<TIn, TOut>>(
            new Swap(rule.Parameters[1], held).Visit(rule.Body), rule.Parameters[0]);
    }

    private sealed class Held<TArg>(TArg value)
    {
        public TArg Value { get; } = value;
    }

    private static Expression<Func<T, bool>> Not(Expression<Func<T, bool>> test) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(test.Body), test.Parameters);

    private static Expression<Func<T, bool>> And(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Join(a, b, false);

    private static Expression<Func<T, bool>> Or(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Join(a, b, true);

    private static Expression<Func<T, bool>> Join(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b, bool any)
    {
        var right = new Swap(b.Parameters[0], a.Parameters[0]).Visit(b.Body);
        return Expression.Lambda<Func<T, bool>>(
            any ? Expression.OrElse(a.Body, right) : Expression.AndAlso(a.Body, right), a.Parameters);
    }

    private static IOrderedQueryable<T> By<TKey>(
        IQueryable<T> rows, Expression<Func<T, TKey>> key, bool descending, bool first) => (first, descending) switch
    {
        (true, false) => rows.OrderBy(key),
        (true, true) => rows.OrderByDescending(key),
        (false, false) => ((IOrderedQueryable<T>)rows).ThenBy(key),
        (false, true) => ((IOrderedQueryable<T>)rows).ThenByDescending(key),
    };

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
