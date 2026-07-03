using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class GeneratedFile : Entity
{
    public Guid DocumentInstanceId { get; private set; }
    public OutputFormat Format { get; private set; }
    public string BlobPath { get; private set; } = default!;

    private GeneratedFile() { }

    internal static GeneratedFile Create(Guid instanceId, OutputFormat format, string blobPath)
        => new() { DocumentInstanceId = instanceId, Format = format, BlobPath = blobPath };
}

public enum OutputFormat { Pdf }
