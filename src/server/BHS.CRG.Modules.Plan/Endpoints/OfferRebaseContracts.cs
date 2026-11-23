using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

public sealed record OfferRebaseRequest(Guid? RevisionId, string? Fingerprint);

public sealed record OfferRebaseLineDto(
    Guid RevisionRowId, Guid? LineId, string? Number, LsrSectionDto? LsrSection,
    string Name, decimal? Volume, string? Unit, Guid? WorkTypeId, string Kind,
    decimal? OwnMarkup, decimal? Price, decimal Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OfferCostDto? Cost);

public sealed record OfferRebaseChangeDto(OfferRebaseLineDto Before, OfferRebaseLineDto After);

public sealed record OfferRebaseUnmatchedDto(
    IReadOnlyList<OfferRebaseLineDto> Before, IReadOnlyList<OfferRebaseLineDto> After);

public sealed record OfferRebaseSectionMarkupDto(
    LsrSectionDto Source, decimal Markup, IReadOnlyList<LsrSectionDto> Candidates);

public sealed record OfferRebasePreviewDto(
    string Version, OfferRevisionRef Source, OfferRevisionRef Target, string Fingerprint,
    IReadOnlyList<OfferRebaseLineDto> Added, IReadOnlyList<OfferRebaseLineDto> Removed,
    IReadOnlyList<OfferRebaseChangeDto> Changed, IReadOnlyList<OfferRebaseUnmatchedDto> Unmatched,
    IReadOnlyList<OfferRebaseSectionMarkupDto> UnmatchedSectionMarkups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

public sealed record OfferRebaseResultDto(OfferDto Offer, IReadOnlyList<OfferRebaseLineDto> Removed);
