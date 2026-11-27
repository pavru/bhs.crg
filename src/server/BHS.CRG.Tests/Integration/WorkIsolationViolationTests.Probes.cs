using Xunit.Sdk;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private sealed class ProbeCatalog
    {
        private sealed record ProbeCall(string Observation, Func<Task> Verify);

        private readonly Dictionary<string, ProbeCall> calls = new(StringComparer.Ordinal);

        internal void Add(string name, string observation, Func<Task> verify) =>
            Assert.True(calls.TryAdd(name, new(observation, verify)), $"{name}: повторная проба.");

        internal async Task RunAsync(IEnumerable<string> required, string? skip = null)
        {
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<XunitException>();
            foreach (var (name, call) in calls)
            {
                if (name == skip) continue;
                try
                {
                    await call.Verify();
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    failures.Add(new XunitException($"{name}: {call.Observation}. {failure.Message}", failure));
                }
                completed.Add(name);
            }
            var expected = required.ToHashSet(StringComparer.Ordinal);
            Assert.True(expected.SetEquals(completed),
                $"Пропущены пробы: {string.Join(", ", expected.Except(completed))}. "
                + $"Лишние пробы: {string.Join(", ", completed.Except(expected))}.");
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }
}
