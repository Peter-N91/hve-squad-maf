namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// Native safety contracts for the v0.16.2 cast. Unknown absent roots fail closed.
/// These are effect requirements, not inference from task prose or charter descriptions.
/// </summary>
internal static class RoleOutputContract
{
    internal static bool ReturnsOnly(string role) => role.ToLowerInvariant() is
        "cost-manager" or "deployer" or "fact-checker" or "intake-validator" or
        "asbuilt-author" or "azure-diagnose" or "aws-diagnose";

    internal static SquadToolEffect? RequiredEffect(string role) => role.ToLowerInvariant() switch
    {
        "developer" or "iac-author" or "pp-connector" or "m365-agent-integrator" or
            "qa-engineer" or "release-engineer" or "presenter" => SquadToolEffect.ProjectWrite,
        "deployer" or "backlog-executor" => SquadToolEffect.External,
        "asbuilt-author" or "azure-diagnose" or "aws-diagnose" => SquadToolEffect.ExternalRead,
        _ => null,
    };

    internal static bool Proves(string role, SquadToolEffect effect, SquadToolExecution receipt)
    {
        if (receipt.Effect != effect)
        {
            return false;
        }

        if (effect != SquadToolEffect.ProjectWrite)
        {
            return !string.IsNullOrWhiteSpace(receipt.Receipt);
        }

        return receipt.Outputs?.Any(o => role.ToLowerInvariant() switch
        {
            "presenter" => o.Path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase),
            "iac-author" => o.Path.StartsWith("infra/bicep/", StringComparison.Ordinal) && o.Path.EndsWith(".bicep", StringComparison.Ordinal) ||
                o.Path.StartsWith("infra/terraform/", StringComparison.Ordinal) && o.Path.EndsWith(".tf", StringComparison.Ordinal),
            _ => true,
        }) == true;
    }
}
