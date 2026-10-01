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

        protected static bool IsEmptyOp(string op) => op is "is_empty" or "is_null";
        protected static bool IsNotEmptyOp(string op) => op is "is_not_empty" or "is_not_null";
    }

    /// <summary>Текст: регистр не различается, пустая клетка — пустая строка.</summary>
    private sealed class TextColumn(Expression<Func<T, string?>> value) : Column
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Text;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            var upper = Compose(value, s => (s ?? "").ToUpper());
            return condition.Op switch
            {
                _ when IsEmptyOp(condition.Op) => Compose(value, TextIsEmpty),
                _ when IsNotEmptyOp(condition.Op) => Not(Compose(value, TextIsEmpty)),
                "eq" => Equal(upper, Upper(condition.Values[0])),
                "neq" => Not(Equal(upper, Upper(condition.Values[0]))),
                "in" => AnyOf(condition.Values.Select(v => Equal(upper, Upper(v)))),
                "not_in" => Not(AnyOf(condition.Values.Select(v => Equal(upper, Upper(v))))),
                "contains" => Contains(upper, Upper(condition.Values[0])),
                "not_contains" => Not(Contains(upper, Upper(condition.Values[0]))),
                "starts_with" => StartsWith(upper, Upper(condition.Values[0])),
                "ends_with" => EndsWith(upper, Upper(condition.Values[0])),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, TextIsEmpty), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(Not(Compose(value, TextIsEmpty)), ct), 0);

        private static string Upper(string text) => text.ToUpperInvariant();

        private static Expression<Func<T, bool>> Equal(Expression<Func<T, string>> cell, string text) =>
            Compose(cell, s => s == text);

        private static Expression<Func<T, bool>> Contains(Expression<Func<T, string>> cell, string text) =>
            Compose(cell, s => s.Contains(text));

        private static Expression<Func<T, bool>> StartsWith(Expression<Func<T, string>> cell, string text) =>
            Compose(cell, s => s.StartsWith(text));

        private static Expression<Func<T, bool>> EndsWith(Expression<Func<T, string>> cell, string text) =>
            Compose(cell, s => s.EndsWith(text));
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
            decimal At(int index) => decimal.Parse(condition.Values[index], System.Globalization.CultureInfo.InvariantCulture);
            Expression<Func<T, bool>> EqualTo(decimal number) => Compose(value, n => n == number);

            switch (condition.Op)
            {
                case var op when IsEmptyOp(op): return IsEmpty;
                case var op when IsNotEmptyOp(op): return Not(IsEmpty);
                case "eq": return EqualTo(At(0));
                case "neq": return Not(EqualTo(At(0)));
                case "in": return AnyOf(condition.Values.Select((_, i) => EqualTo(At(i))));
                case "not_in": return Not(AnyOf(condition.Values.Select((_, i) => EqualTo(At(i)))));
            }

            var (a, b) = (At(0), condition.Values.Count > 1 ? At(1) : 0m);
            return condition.Op switch
            {
                "gt" => Compose(value, n => n > a),
                "gte" => Compose(value, n => n >= a),
                "lt" => Compose(value, n => n < a),
                "lte" => Compose(value, n => n <= a),
                "between" => Compose(value, n => n >= a && n <= b),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, n => n == null), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            var numbers = rows.Select(value).Where(n => n != null);
            var count = await numbers.LongCountAsync(ct);
            // Не число и не пусто — «не учтено». У настоящей числовой колонки таких не бывает.
            var skipped = raw is null
                ? 0
                : await rows.LongCountAsync(And(Not(IsEmpty), Compose(value, n => n == null)), ct);
            if (count == 0) return new(0, skipped);

            return new(count, skipped,
                await numbers.SumAsync(ct), await numbers.MinAsync(ct), await numbers.MaxAsync(ct));
        }
    }

    /// <summary>Дата в колонке базы.</summary>
    private sealed class DateColumn(Expression<Func<T, DateOnly?>> value) : Column
    {
        public override ModuleTableColumnKind Kind => ModuleTableColumnKind.Date;

        public override Expression<Func<T, bool>> Test(TableFilterCondition condition)
        {
            DateOnly At(int index) => DateOnly.ParseExact(
                condition.Values[index], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Expression<Func<T, bool>> EqualTo(DateOnly date) => Compose(value, d => d == date);

            switch (condition.Op)
            {
                case var op when IsEmptyOp(op): return Compose(value, d => d == null);
                case var op when IsNotEmptyOp(op): return Compose(value, d => d != null);
                case "eq": return EqualTo(At(0));
                case "neq": return Not(EqualTo(At(0)));
                case "in": return AnyOf(condition.Values.Select((_, i) => EqualTo(At(i))));
                case "not_in": return Not(AnyOf(condition.Values.Select((_, i) => EqualTo(At(i)))));
            }

            var (a, b) = (At(0), condition.Values.Count > 1 ? At(1) : default);
            return condition.Op switch
            {
                "gt" => Compose(value, d => d > a),
                "gte" => Compose(value, d => d >= a),
                "lt" => Compose(value, d => d < a),
                "lte" => Compose(value, d => d <= a),
                "between" => Compose(value, d => d >= a && d <= b),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(value, d => d == null), false, first), value, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            var dates = rows.Select(value).Where(d => d != null);
            var count = await dates.LongCountAsync(ct);
            return count == 0
                ? new(0, 0)
                : new(count, 0, Min: await dates.MinAsync(ct), Max: await dates.MaxAsync(ct));
        }
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
            Expression<Func<T, bool>> EqualTo(string date) => Compose(_date, d => d == date);

            switch (condition.Op)
            {
                case var op when IsEmptyOp(op): return Compose(raw, TextIsEmpty);
                case var op when IsNotEmptyOp(op): return Not(Compose(raw, TextIsEmpty));
                case "eq": return EqualTo(condition.Values[0]);
                case "neq": return Not(EqualTo(condition.Values[0]));
                case "in": return AnyOf(condition.Values.Select(EqualTo));
                case "not_in": return Not(AnyOf(condition.Values.Select(EqualTo)));
            }

            var (a, b) = (condition.Values[0], condition.Values.Count > 1 ? condition.Values[1] : "");
            return condition.Op switch
            {
                "gt" => Compose(_date, d => string.Compare(d, a) > 0),
                "gte" => Compose(_date, d => string.Compare(d, a) >= 0),
                "lt" => Compose(_date, d => string.Compare(d, a) < 0),
                "lte" => Compose(_date, d => string.Compare(d, a) <= 0),
                "between" => Compose(_date, d => string.Compare(d, a) >= 0 && string.Compare(d, b) <= 0),
                _ => throw Unknown(condition),
            };
        }

        public override IOrderedQueryable<T> Order(IQueryable<T> rows, bool descending, bool first) =>
            By(By(rows, Compose(_date, d => d == null), false, first), _date, descending, false);

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct)
        {
            var dates = rows.Select(_date).Where(d => d != null);
            var count = await dates.LongCountAsync(ct);
            var skipped = await rows.LongCountAsync(
                And(Not(Compose(raw, TextIsEmpty)), Compose(_date, d => d == null)), ct);
            return count == 0
                ? new(0, skipped)
                : new(count, skipped, Min: await dates.MinAsync(ct), Max: await dates.MaxAsync(ct));
        }
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
                .OrderBy(l => l.Value, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), true))
                .Select(l => (TKey?)l.Key).ToArray();
            return By(By(rows, Not(Known), false, first), Compose(id, key => Array.IndexOf(ordered, key)), descending, false);
        }

        public override async Task<TableTotal> TotalAsync(IQueryable<T> rows, CancellationToken ct) =>
            new(await rows.LongCountAsync(Known, ct), 0);
    }

    private static InvalidOperationException Unknown(TableFilterCondition condition) =>
        new($"Оператора «{condition.Op}» у колонки «{condition.Column}» запрос к базе не умеет.");

    // ── Сборка выражений ────────────────────────────────────────────────────────

    /// <summary>Подставить значение колонки в правило: <c>row => rule(column(row))</c>.</summary>
    private static Expression<Func<T, TOut>> Compose<TIn, TOut>(
        Expression<Func<T, TIn>> column, Expression<Func<TIn, TOut>> rule) =>
        Expression.Lambda<Func<T, TOut>>(
            new Swap(rule.Parameters[0], column.Body).Visit(rule.Body), column.Parameters);

    private static Expression<Func<T, bool>> Not(Expression<Func<T, bool>> test) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(test.Body), test.Parameters);

    private static Expression<Func<T, bool>> And(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Join(a, b, false);

    private static Expression<Func<T, bool>> Or(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b) => Join(a, b, true);

    private static Expression<Func<T, bool>> AnyOf(IEnumerable<Expression<Func<T, bool>>> tests) =>
        tests.Aggregate(Or);

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
