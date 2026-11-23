using System.Reflection;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>Сверка реальных обработчиков с регистрацией генератора, независимо от его Assemblies.</summary>
[Collection("Integration")]
public class MediatorRegistrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public void Generator_registers_every_product_handler()
    {
        using var scope = fixture.Services.CreateScope();
        var assemblies = new[] { typeof(CatalogHandlers).Assembly, typeof(Program).Assembly,
            typeof(AppDbContext).Assembly };
        Assert.Empty(MissingHandlers(scope.ServiceProvider, assemblies));
    }

    [Fact]
    public void Inventory_detects_handler_in_unscanned_assembly()
    {
        using var scope = fixture.Services.CreateScope();
        Assert.Contains(typeof(UnscannedHandler), MissingHandlers(scope.ServiceProvider, [GetType().Assembly]));
    }

    private static IEnumerable<Type> MissingHandlers(IServiceProvider provider, Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes())
                     .Where(type => type is { IsAbstract: false, IsInterface: false } && !type.ContainsGenericParameters))
        // The no-result contract and its inherited Unit contract describe one request.
        foreach (var contracts in type.GetInterfaces().Where(IsHandler)
                     .GroupBy(contract => contract.GenericTypeArguments[0]))
            if (!contracts.Any(contract => provider.GetService(contract)?.GetType() == type))
                yield return type;
    }

    private static bool IsHandler(Type type) => type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(IRequestHandler<>) ||
         type.GetGenericTypeDefinition() == typeof(IRequestHandler<,>));

    // Генератор только в Api, а эта сборка ему не названа: контрольная потеря обработчика.
    public sealed record UnscannedRequest : IRequest<int>;
    public sealed class UnscannedHandler : IRequestHandler<UnscannedRequest, int>
    {
        public ValueTask<int> Handle(UnscannedRequest request, CancellationToken cancellationToken) => new(1);
    }
}
