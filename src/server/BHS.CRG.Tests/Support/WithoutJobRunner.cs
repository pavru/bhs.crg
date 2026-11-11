using BHS.CRG.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Хост теста БЕЗ исполнителя фоновых задач — для проверок, которым важна постановка задачи, а не
/// выполнение: поставленная копия иначе снялась бы по-настоящему, с чтением тестовой базы посреди
/// чужих тестов, а распознавание ушло бы к движку по сети.
///
/// <para>Одним рецептом на все такие классы (ревью PR #1894): исполнитель зарегистрирован и как
/// сам тип, и как размещённая служба, и копия рецепта, забывшая одно из двух, оставила бы
/// исполнитель на месте — молча.</para>
/// </summary>
internal static class WithoutJobRunner
{
    public static IServiceCollection RemoveJobRunner(this IServiceCollection services)
    {
        services.RemoveAll<JobBackgroundService>();
        foreach (var d in services.Where(d => d.ImplementationType == typeof(JobBackgroundService)).ToList())
            services.Remove(d);
        return services;
    }
}

/// <summary>Подставные часы: момент задаёт тест и двигает сам.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
