namespace HveSquad.AgentFramework.Runtime;

/// <summary>
/// Native effect contracts owned by the supported-release inventory. Unknown absent roots fail closed.
/// These are effect requirements, not inference from task prose or charter descriptions.
/// </summary>
internal static class RoleOutputContract
{
    internal static bool ReturnsOnly(string role) => Sources.SupportedSquadRelease.Current.ReturnsOnly(role);

    internal static SquadToolEffect? RequiredEffect(string role) => Sources.SupportedSquadRelease.Current.RequiredEffect(role);

    internal static bool Proves(string role, SquadToolEffect effect, SquadToolExecution receipt)
    {
        if (receipt.Effect != effect || receipt.Status != SquadToolOutputStatus.Succeeded)
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
