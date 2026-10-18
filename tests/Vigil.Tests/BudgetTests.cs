using Vigil.Core;
using Vigil.Core.Tools;

namespace Vigil.Tests;

/// <summary>Every request carries the system prompt and every tool's schema. On the free tier
/// the limit is tokens per minute, so this is what decides how often Vigil can think.</summary>
public class BudgetTests
{
    static int Tokens(string text) => text.Length / 4;   // a fair rule of thumb for English JSON

    [Fact]
    public void What_every_request_carries_stays_small()
    {
        var config = new Config();
        var registry = Registry.Build(config);
        var schemas = Registry.Schemas(registry.Specs).ToJsonString();
        var total = Tokens(schemas);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vigil-budget.txt"),
            "tools: " + registry.Count + "\nschema tokens: " + total + "\n" +
            string.Join("\n", registry.Specs.OrderByDescending(s => s.Schema().ToJsonString().Length)
                .Select(s => s.Name + " " + Tokens(s.Schema().ToJsonString()))));
        Assert.True(total < 3200, "tool schemas are " + total + " tokens");
    }
}
