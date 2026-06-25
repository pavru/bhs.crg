using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class TypstUserLib : Entity
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0001-000000000001");

    public string Content { get; private set; } = string.Empty;

    private TypstUserLib() { }

    public static TypstUserLib Create(string content)
        => new() { Id = SingletonId, Content = content };

    public void UpdateContent(string content) { Content = content; TouchUpdatedAt(); }
}
