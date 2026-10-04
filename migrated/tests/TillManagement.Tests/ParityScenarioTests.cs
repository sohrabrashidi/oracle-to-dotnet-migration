using System.Text.Json;
using TillManagement.Core;

namespace TillManagement.Tests;

/// <summary>
/// Replays the scenario files in /scenarios against the C# port. In the real
/// migration the same scripts were run against the Oracle package and the
/// results saved as the "expect" blocks. Any difference is a parity bug,
/// unless it's one of the documented behaviour changes.
///
/// The same scenarios run against the in-memory repository and, when a
/// connection string is available, against PostgreSQL.
/// </summary>
public abstract class ParityScenarioTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> ScenarioFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json")
            .Order()
            .Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>Returns a fresh, empty repository, or null if this backend is not available.</summary>
    protected abstract Task<ITillRepository?> CreateRepositoryAsync();

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public async Task Scenario_matches_legacy_behaviour(string file)
    {
        var repository = await CreateRepositoryAsync();
        if (repository is null)
        {
            return;
        }

        var scenario = JsonSerializer.Deserialize<Scenario>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", file)),
            Json)!;

        var service = new TillService(repository);
        var tills = new Dictionary<string, long>();

        foreach (var step in scenario.Steps)
        {
            try
            {
                switch (step.Action)
                {
                    case "open":
                        tills[step.Till] = await service.OpenTillAsync(step.Branch!, step.Cashier!, step.Amounts ?? []);
                        break;

                    case "move":
                        await service.RecordMovementAsync(
                            tills[step.Till],
                            step.Currency!,
                            step.Type == "OUT" ? MovementType.Out : MovementType.In,
                            step.Amount!.Value,
                            step.Reference);
                        break;

                    case "close":
                        var result = await service.CloseTillAsync(tills[step.Till], step.Amounts ?? []);
                        Assert.Equal(step.Expect!.Status, result.Status.ToString().ToUpperInvariant());
                        foreach (var (currency, variance) in step.Expect.Variances ?? [])
                        {
                            Assert.Equal(variance, result.Lines.Single(l => l.Currency == currency).Variance);
                        }

                        break;

                    case "cash":
                        var cash = await service.CashOnHandAsync(tills[step.Till], step.Currency!);
                        Assert.Equal(step.Expect!.Cash, cash);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown action '{step.Action}' in {file}");
                }

                Assert.True(step.Expect?.Error is null, $"{file}: expected ORA-{step.Expect?.Error} at step '{step.Action}' but it succeeded");
            }
            catch (TillException ex)
            {
                Assert.True(step.Expect?.Error is not null, $"{file}: unexpected ORA-{(int)ex.Code} at step '{step.Action}': {ex.Message}");
                Assert.Equal(step.Expect!.Error, (int)ex.Code);
            }
        }
    }

    private sealed record Scenario(string Description, List<Step> Steps);

    private sealed record Step(
        string Action,
        string Till,
        string? Branch,
        string? Cashier,
        string? Currency,
        string? Type,
        decimal? Amount,
        string? Reference,
        Dictionary<string, decimal?>? Amounts,
        Expectation? Expect);

    private sealed record Expectation(string? Status, int? Error, decimal? Cash, Dictionary<string, decimal>? Variances);
}

public class InMemoryParityTests : ParityScenarioTests
{
    protected override Task<ITillRepository?> CreateRepositoryAsync() =>
        Task.FromResult<ITillRepository?>(new InMemoryTillRepository());
}
