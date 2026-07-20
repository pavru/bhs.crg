using System.Linq.Expressions;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Проверка сборки Typst-блоков (issue #309, фаза 2): draft-overlay меняет граф, диагностики графа и
/// синтаксиса маппятся на конкретный блок (тип+вариант), недоступность CLI не роняет проверку.
/// </summary>
public class ValidateTypstBlocksHandlerTests
{
    private sealed class FakeTypeRepo(IReadOnlyList<DocumentType> types) : IRepository<DocumentType>
    {
        public Task<IReadOnlyList<DocumentType>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(types);
        public Task<DocumentType?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DocumentType>> FindAsync(Expression<Func<DocumentType, bool>> p, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(DocumentType e, CancellationToken ct = default) => throw new NotImplementedException();
        public void Update(DocumentType e) => throw new NotImplementedException();
        public void Remove(DocumentType e) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeChecker(Func<string, IReadOnlyList<TypstSyntaxError>> f) : ITypstSyntaxChecker
    {
        public Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(string content, CancellationToken ct) => Task.FromResult(f(content));
    }

    private static readonly Func<string, IReadOnlyList<TypstSyntaxError>> NoSyntaxErrors = _ => Array.Empty<TypstSyntaxError>();

    private static string RendersJson((string variant, string fn, string block)[] rs) =>
        string.Join(",", rs.Select(r =>
            $"{{\"name\":{JsonSerializer.Serialize(r.variant)},\"fnName\":{JsonSerializer.Serialize(r.fn)},\"block\":{JsonSerializer.Serialize(r.block)}}}"));

    private static DocumentType Type(string name, string code, params (string variant, string fn, string block)[] rs) =>
        DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse($"{{\"typstRenders\":[{RendersJson(rs)}]}}"));

    private static JsonElement Draft(params (string variant, string fn, string block)[] rs) =>
        JsonDocument.Parse($"[{RendersJson(rs)}]").RootElement.Clone();

    private static ValidateTypstBlocksHandler Handler(IReadOnlyList<DocumentType> types,
        Func<string, IReadOnlyList<TypstSyntaxError>>? checker = null) =>
        new(new FakeTypeRepo(types), new FakeChecker(checker ?? NoSyntaxErrors));

    [Fact]
    public async Task DraftOverlay_ChangesGraph_IntroducesCycle()
    {
        // Персист: addr-contacts вызывает addr-full — упорядочиваемо, цикла нет.
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ it.x }"));
        var contacts = Type("Контакты", "CONT", ("Строка", "addr-contacts", "{ addr-full(it) }"));
        var handler = Handler(new[] { addr, contacts });

        var clean = await handler.Handle(new ValidateTypstBlocksQuery(null, null), default);
        Assert.DoesNotContain(clean, p => p.Code == "cycle");

        // Черновик делает addr-full вызывающим addr-contacts → взаимный цикл.
        var draft = Draft(("Полный", "addr-full", "{ addr-contacts(it) }"));
        var withCycle = await handler.Handle(new ValidateTypstBlocksQuery(addr.Id, draft), default);
        Assert.Contains(withCycle, p => p.Code == "cycle");
    }

    [Fact]
    public async Task SyntaxError_IsMappedToBlock_ByLineMap()
    {
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ it.x }"));
        // Комментарий = строка 1, #let addr-full = строка 2 → ошибка на строке 2 маппится на addr-full.
        var handler = Handler(new[] { addr }, _ => new[] { new TypstSyntaxError(2, 1, "unexpected token") });

        var res = await handler.Handle(new ValidateTypstBlocksQuery(null, null), default);
        var syntax = Assert.Single(res, p => p.Code == "syntax");
        Assert.Equal("addr-full", syntax.FnName);
        Assert.Equal("Адрес", syntax.TypeName);
        Assert.Equal(1, syntax.Line);
    }

    [Fact]
    public async Task DuplicateFnName_AcrossTypes_IsReported()
    {
        var a = Type("A", "A", ("v", "dup", "{ it.x }"));
        var b = Type("B", "B", ("v", "dup", "{ it.y }"));
        var res = await Handler(new[] { a, b }).Handle(new ValidateTypstBlocksQuery(null, null), default);
        Assert.Contains(res, p => p.Code == "duplicate-fn");
    }

    [Fact]
    public async Task CheckerUnavailable_DoesNotThrow_ReportsWarning()
    {
        var t = Type("A", "A", ("v", "f", "{ it.x }"));
        var handler = Handler(new[] { t }, _ => throw new InvalidOperationException("no cli"));
        var res = await handler.Handle(new ValidateTypstBlocksQuery(null, null), default);
        Assert.Contains(res, p => p.Code == "checker-unavailable" && p.Severity == "warning");
    }

    [Fact]
    public async Task Clean_Blocks_ProduceNoProblems()
    {
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ addr-contacts(it) }"));
        var contacts = Type("Контакты", "CONT", ("Строка", "addr-contacts", "{ it.x }"));
        var res = await Handler(new[] { addr, contacts }).Handle(new ValidateTypstBlocksQuery(null, null), default);
        Assert.Empty(res);
    }
}
