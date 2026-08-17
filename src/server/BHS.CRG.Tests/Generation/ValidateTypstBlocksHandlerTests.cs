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

    private sealed class FakeChecker(Func<IReadOnlyList<TypstBlockFile>, IReadOnlyList<TypstSyntaxError>> f)
        : ITypstSyntaxChecker
    {
        public Task<IReadOnlyList<TypstSyntaxError>> CheckAsync(
            IReadOnlyList<TypstBlockFile> files, CancellationToken ct) => Task.FromResult(f(files));
    }

    private static readonly Func<IReadOnlyList<TypstBlockFile>, IReadOnlyList<TypstSyntaxError>> NoSyntaxErrors
        = _ => Array.Empty<TypstSyntaxError>();

    /// <summary>Ошибка на строке, где реально стоит `#let fn`, — искать её в собранном файле надёжнее,
    /// чем считать строки в тесте: раскладка модуля (шапка, импорты, шим) от issue к issue меняется,
    /// а проверяем мы не её, а то, что line-map доводит ошибку до нужного блока.</summary>
    private static Func<IReadOnlyList<TypstBlockFile>, IReadOnlyList<TypstSyntaxError>> ErrorAtDefinitionOf(string fn)
        => files =>
        {
            foreach (var f in files)
            {
                var lines = f.Content.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].StartsWith($"#let {fn}(", StringComparison.Ordinal))
                        return new[] { new TypstSyntaxError(f.Path, i + 1, 1, "unexpected token") };
            }
            return Array.Empty<TypstSyntaxError>();
        };

    private static string RendersJson((string variant, string fn, string block)[] rs) =>
        string.Join(",", rs.Select(r =>
            $"{{\"name\":{JsonSerializer.Serialize(r.variant)},\"fnName\":{JsonSerializer.Serialize(r.fn)},\"block\":{JsonSerializer.Serialize(r.block)}}}"));

    private static DocumentType Type(string name, string code, params (string variant, string fn, string block)[] rs) =>
        DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse($"{{\"typstRenders\":[{RendersJson(rs)}]}}"));

    private static JsonElement Draft(params (string variant, string fn, string block)[] rs) =>
        JsonDocument.Parse($"[{RendersJson(rs)}]").RootElement.Clone();

    private static ValidateTypstBlocksHandler Handler(IReadOnlyList<DocumentType> types,
        Func<IReadOnlyList<TypstBlockFile>, IReadOnlyList<TypstSyntaxError>>? checker = null) =>
        new(new FakeTypeRepo(types), new FakeChecker(checker ?? NoSyntaxErrors));

    [Fact]
    public async Task DraftOverlay_ChangesGraph_IntroducesCrossTypeCycle()
    {
        // Персист: addr-contacts вызывает addr-full — упорядочиваемо, цикла нет.
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ it.x }"));
        var contacts = Type("Контакты", "CONT", ("Строка", "addr-contacts", "{ addr-full(it) }"));
        var handler = Handler(new[] { addr, contacts });

        var clean = await handler.Handle(new ValidateTypstBlocksQuery(null, null), default);
        Assert.DoesNotContain(clean, p => p.Code.StartsWith("cycle", StringComparison.Ordinal));

        // Черновик делает addr-full вызывающим addr-contacts → взаимная ссылка между ТИПАМИ. С
        // расколом по файлам (#772) это не ошибка порядка, а петля импортов: сборка разрывает её
        // отложенным импортом, поэтому диагностика предупреждающая, а не Error.
        var draft = Draft(("Полный", "addr-full", "{ addr-contacts(it) }"));
        var withCycle = await handler.Handle(new ValidateTypstBlocksQuery(addr.Id, draft), default);
        var cycle = Assert.Single(withCycle, p => p.Code == "cycle-cross-type");
        Assert.Equal("warning", cycle.Severity);
    }

    [Fact]
    public async Task CycleWithinType_IsError_OrderUnresolvable()
    {
        // Два блока ОДНОГО типа зовут друг друга: они в одной области Typst, импортом не развести —
        // остаётся Error и best-effort порядок, как было во flat-файле.
        var t = Type("Адрес", "ADDR",
            ("Полный", "addr-full", "{ addr-contacts(it) }"),
            ("Контакты", "addr-contacts", "{ addr-full(it) }"));
        var res = await Handler(new[] { t }).Handle(new ValidateTypstBlocksQuery(null, null), default);
        var cycle = Assert.Single(res, p => p.Code == "cycle");
        Assert.Equal("error", cycle.Severity);
    }

    [Fact]
    public async Task SyntaxError_IsMappedToBlock_ByFileAndLine()
    {
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ it.x }"));
        var other = Type("Контакты", "CONT", ("Строка", "cont-line", "{ it.y }"));
        var handler = Handler(new[] { addr, other }, ErrorAtDefinitionOf("addr-full"));

        var res = await handler.Handle(new ValidateTypstBlocksQuery(null, null), default);
        var syntax = Assert.Single(res, p => p.Code == "syntax");
        Assert.Equal("addr-full", syntax.FnName);
        Assert.Equal("Адрес", syntax.TypeName);
        Assert.Equal(1, syntax.Line);   // первая строка самого блока
    }

    [Fact]
    public async Task SyntaxError_InOneModule_DoesNotHitSameLineOfAnother()
    {
        // Номера строк в модулях начинаются заново, поэтому «строка N» без файла указывает сразу на
        // несколько блоков. Ошибка обязана достаться тому типу, в чьём файле она возникла.
        var addr = Type("Адрес", "ADDR", ("Полный", "addr-full", "{ it.x }"));
        var cont = Type("Контакты", "CONT", ("Строка", "cont-line", "{ it.y }"));
        var res = await Handler(new[] { addr, cont }, ErrorAtDefinitionOf("cont-line"))
            .Handle(new ValidateTypstBlocksQuery(null, null), default);

        var syntax = Assert.Single(res, p => p.Code == "syntax");
        Assert.Equal("cont-line", syntax.FnName);
        Assert.Equal("Контакты", syntax.TypeName);
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
