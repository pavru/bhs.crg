using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Application.Catalog;

public record CreateCatalogEntityCommand(
    string EntityType, string DisplayName, JsonDocument Data, Guid? OwnerId
) : IRequest<CatalogEntity>;

public record UpdateCatalogEntityCommand(
    Guid Id, string DisplayName, JsonDocument Data
) : IRequest<CatalogEntity>;

public record DeleteCatalogEntityCommand(Guid Id) : IRequest;

public record GetCatalogEntityQuery(Guid Id) : IRequest<CatalogEntity?>;

public record ListCatalogEntitiesQuery(string? EntityType, Guid? OwnerId) : IRequest<IReadOnlyList<CatalogEntity>>;

// ── PrimitiveType ──────────────────────────────────────────────────────────────

public record ListPrimitiveTypesQuery : IRequest<IReadOnlyList<PrimitiveType>>;

public record CreatePrimitiveTypeCommand(
    string Name, string Code, string BaseType, string? Description, JsonDocument Constraints,
    IReadOnlyList<string>? AllowedTags = null
) : IRequest<PrimitiveType>;

public record UpdatePrimitiveTypeCommand(
    Guid Id, string Name, string Code, string? Description, JsonDocument Constraints,
    IReadOnlyList<string>? AllowedTags = null
) : IRequest<PrimitiveType>;

public record DeletePrimitiveTypeCommand(Guid Id) : IRequest;
public record SetPrimitiveTypeGroupCommand(Guid Id, string? Group) : IRequest<PrimitiveType>;

// ── EnumType (issue #59) ────────────────────────────────────────────────────────

public record ListEnumTypesQuery : IRequest<IReadOnlyList<EnumType>>;

public record CreateEnumTypeCommand(
    string Name, string Code, string? Description, JsonDocument Values
) : IRequest<EnumType>;

public record UpdateEnumTypeCommand(
    Guid Id, string Name, string Code, string? Description, JsonDocument Values
) : IRequest<EnumType>;

public record DeleteEnumTypeCommand(Guid Id) : IRequest;
public record SetEnumTypeGroupCommand(Guid Id, string? Group) : IRequest<EnumType>;
