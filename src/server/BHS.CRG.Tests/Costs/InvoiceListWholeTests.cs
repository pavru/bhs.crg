using System.Reflection;
using System.Text.RegularExpressions;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Сторож условия, на котором держится отбор рейла счетов (issue #1221): список счетов приходит
/// ЦЕЛИКОМ, и «Избранные стройки» урезают его на клиенте по <c>constructionIds</c>.
///
/// <para>Чем ломается: список становится страничным (G4, issue #1097), а отбор остаётся на клиенте.
/// Тогда из страницы в сто счетов на экран попали бы три, «скрыто: N» считало бы одну страницу, и
/// остальные счета избранных строек не показались бы вовсе — молча. Этот тест падает на первом
/// параметре страницы у адреса списка: значит, пора переносить отбор на сервер параметром
/// <c>sites</c>, как у накладных (#1222). Условие названо и в сторож-реестре клиента
/// (<c>siteFilterRegistry.test.ts</c>, <c>until</c>), и тот проверяет, что этот файл существует.</para>
/// </summary>
public class InvoiceListWholeTests
{
    private static readonly Regex Paging = new("page|offset|limit|skip|take|cursor|after|size", RegexOptions.IgnoreCase);

    [Fact]
    public void Список_счетов_не_страничный()
    {
        var list = typeof(InvoiceListEndpoint).GetMethod(nameof(InvoiceListEndpoint.ListAsync), BindingFlags.Public | BindingFlags.Static)!;
        var names = list.GetParameters().Select(p => p.Name!).ToList();

        // Положительный якорь: без него переименование метода дало бы пустой список и вечный успех.
        Assert.Contains("needsParsing", names);
        Assert.DoesNotContain(names, n => Paging.IsMatch(n));
    }
}
