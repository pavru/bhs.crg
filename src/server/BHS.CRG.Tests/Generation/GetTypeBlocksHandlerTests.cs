using System.Linq.Expressions;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Просмотрщик собранных блоков (issue #770): отдаёт те же файлы, что уходят в Typst, и — с #772 —
/// претензии сборки к ним. Претензии тут не «дополнительная информация»: предупреждение о путях
/// касается многих типов сразу, а проверка блоков живёт у ОДНОГО типа и требует нажатия, так что
/// молчащий просмотрщик оставил бы человека без единственного места, где это видно целиком.
/// </summary>
public class GetTypeBlocksHandlerTests
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

    private static DocumentType Type(string name, string code, string fn, string block) =>
        DocumentType.Create(name, code, DocumentTypeKind.Composite, null, JsonDocument.Parse(
            $$"""{"typstRenders":[{"name":"осн","fnName":{{JsonSerializer.Serialize(fn)}},"block":{{JsonSerializer.Serialize(block)}}}]}"""));

    private static Task<TypeBlocksView> Handle(params DocumentType[] types) =>
        new GetTypeBlocksHandler(new FakeTypeRepo(types)).Handle(new GetTypeBlocksQuery(), default);

    [Fact]
    public async Task Returns_EntrypointAndModule_WithBlockCount()
    {
        var view = await Handle(Type("Организация", "Организация", "org-full", "{ it.x }"));

        Assert.Equal("typeblocks.typ", view.Files[0].Path);
        Assert.Contains(view.Files, f => f.Path == "typeblocks/Организация.typ");
        Assert.Equal(1, view.BlockCount);
    }

    /// <summary>Предупреждение о путях (#772) обязано доходить до экрана: импорт внутри блока ленив,
    /// Typst промолчит до генерации, и другого места узнать о нём нет.</summary>
    [Fact]
    public async Task RelativePathInsideBlock_ReachesTheViewer()
    {
        var view = await Handle(Type("Адрес", "Адрес", "addr-full", "{ import \"userlib.typ\": dig\n dig(it) }"));

        var p = Assert.Single(view.Problems, x => x.Code == "relative-path");
        Assert.Equal("warning", p.Severity);
        Assert.Contains("/userlib.typ", p.Message);
    }

    [Fact]
    public async Task CleanBlocks_ProduceNoProblems()
        => Assert.Empty((await Handle(Type("Адрес", "Адрес", "addr-full", "{ it.x }"))).Problems);
}
