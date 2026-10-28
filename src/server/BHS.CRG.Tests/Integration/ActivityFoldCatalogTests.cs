using BHS.CRG.Api.Activity;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Объявление свёртки вида журнала (ТЗ CORE-28.3): каталог переводит его, проверяет при старте и
/// отдаёт порту модуля вместе с видом.
///
/// <para>Негодное объявление обязано ронять СБОРКУ каталога, а каталог разрешается при старте: отказ
/// приходит тому, кто собирал поставку, а не читателю журнала, который увидел бы ряд, склеенный через
/// чужой модуль или распавшийся у одного читателя и целый у другого.</para>
/// </summary>
public class ActivityFoldCatalogTests(ModulePortsHost host) : IClassFixture<ModulePortsHost>
{
    private const string Group = "costs.probe.edit";

    private static ModuleActivityAction Head(string group = Group, string? read = "costs.invoice.read") =>
        new("costs.probe.head", "Пробная шапка изменена", read,
            new ModuleActivityFold(group, ModuleFoldRule.Union, ModuleFoldRule.Union));

    private static ModuleActivityAction Lines(string? read = "costs.invoice.read") =>
        new("costs.probe.lines", "Пробные строки изменены", read,
            new ModuleActivityFold(Group, ModuleFoldRule.Span, ModuleFoldRule.Span));

    private ActivityActionCatalog Catalog(params ModuleActivityAction[] actions) =>
        new([new Declared(actions)], host.Services.GetRequiredService<ModuleRegistry>(),
            host.Services.GetRequiredService<PermissionCatalog>());

    [Fact]
    public void Годное_объявление_доходит_до_каталога_со_свёрткой()
    {
        var catalog = Catalog(Head(), Lines());

        Assert.Equal(new ActivityFold(Group, FoldRule.Union, FoldRule.Union), catalog.Find("costs.probe.head")!.Fold);
        Assert.Equal(new ActivityFold(Group, FoldRule.Span, FoldRule.Span), catalog.Find("costs.probe.lines")!.Fold);
    }

    [Fact]
    public void Чужая_группа_роняет_каталог()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Catalog(Head(group: "id.probe.edit")));

        Assert.Contains("id.probe.edit", refusal.Message);
        Assert.Contains("своего модуля", refusal.Message);
    }

    [Fact]
    public void Разные_права_в_одной_группе_роняют_каталог()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Catalog(Head(), Lines(read: "costs.waybill.read")));

        Assert.Contains(Group, refusal.Message);
        Assert.Contains("разными правами", refusal.Message);
    }

    [Fact]
    public void Свёртка_у_вида_ядра_из_запретного_перечня_роняет_каталог()
    {
        var folded = ActivityActions.ModulesChanged with
        {
            Fold = new ActivityFold("core.modules.edit", FoldRule.Span, FoldRule.Span),
        };

        var refusal = Assert.Throws<InvalidOperationException>(() => ActivityActionCatalog.CheckCoreFolds([folded]));

        Assert.Contains(ActivityActions.ModulesChanged.Code, refusal.Message);
        Assert.Contains("не сворачиваются", refusal.Message);
    }

    [Fact]
    public void Группа_ядра_с_чужим_префиксом_роняет_каталог()
    {
        var folded = ActivityActions.TableViewChanged with
        {
            Fold = new ActivityFold("costs.views.edit", FoldRule.Span, FoldRule.Span),
        };

        var refusal = Assert.Throws<InvalidOperationException>(() => ActivityActionCatalog.CheckCoreFolds([folded]));

        Assert.Contains("costs.views.edit", refusal.Message);
    }

    /// <summary>
    /// Порт модуля пишет вид ИЗ КАТАЛОГА — со свёрткой. Собранный заново из вызова, он терял бы её
    /// молча, и объявленные сворачиваемыми записи ложились бы одиночными: ни один тест чтения этого не
    /// заметил бы, пока ряды одиночны и без того.
    /// </summary>
    [Fact]
    public async Task Порт_модуля_передаёт_журналу_вид_со_свёрткой()
    {
        var journal = new RecordingJournal();
        var port = new ModuleActivityLogPort(journal, Catalog(Head(), Lines()));

        await port.RecordAsync(Lines(), targetId: "счёт-1", after: "3 строки");

        var written = Assert.Single(journal.Recorded);
        Assert.Equal("costs.probe.lines", written.Code);
        Assert.Equal(new ActivityFold(Group, FoldRule.Span, FoldRule.Span), written.Fold);
    }

    private sealed class Declared(ModuleActivityAction[] actions) : IModuleActivityActions
    {
        public IReadOnlyList<ModuleActivityAction> Actions => actions;
    }

    private sealed class RecordingJournal : IActivityLog
    {
        public List<ActivityAction> Recorded { get; } = [];

        public Task RecordAsync(ActivityAction action, string? targetId = null, string? targetLabel = null,
            string? before = null, string? after = null, CancellationToken ct = default)
        {
            Recorded.Add(action);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityRow>> ReadAsync(int skip, int take, ActivityVisibility visible,
            string? action = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountAsync(ActivityVisibility visible, string? action = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityRecord>> SeriesAsync(Guid sessionId, string action,
            ActivityVisibility visible, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ActivityRecord?> LastAsync(ActivityAction action, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ActivityRecord>> ExportAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> ImportAsync(IReadOnlyList<ActivityRecord> records, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
