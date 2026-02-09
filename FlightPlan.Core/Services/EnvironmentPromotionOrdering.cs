using FlightPlan.Models;

namespace FlightPlan.Reporting;

public static class EnvironmentPromotionOrdering
{
    /// <summary>
    /// Orders environments by their promotion chain(s) as authored via environments[].promotesTo.
    ///
    /// Behavior:
    /// - Starts from root environments (those not promoted-to by any other environment), preserving YAML order.
    /// - Depth-first follows promotesTo edges (preserving listed order) so dev → qa → uat → prod renders naturally.
    /// - Appends any remaining environments (cycles, disconnected nodes) in YAML order.
    /// </summary>
    public static IReadOnlyList<EnvironmentEntity> Order(IReadOnlyList<EnvironmentEntity> environments)
    {
        if (environments.Count <= 1) return environments;

        var byId = environments
            .Where(e => !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Build promotion graph: envId -> destinations (supports one-to-many)
        var promotionGraph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var env in environments)
        {
            if (string.IsNullOrWhiteSpace(env.Id)) continue;
            if (env.PromotesTo is not { Count: > 0 }) continue;

            var destinations = env.PromotesTo
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (destinations.Count > 0)
            {
                promotionGraph[env.Id] = destinations;
            }
        }

        // Find all environments that are promoted to (any destination)
        var promotedToEnvs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destinations in promotionGraph.Values)
        {
            foreach (var dest in destinations)
            {
                promotedToEnvs.Add(dest);
            }
        }

        // Roots: not promoted to by anyone (preserve YAML order)
        var roots = environments
            .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !promotedToEnvs.Contains(e.Id))
            .Select(e => e.Id)
            .ToList();

        var ordered = new List<EnvironmentEntity>(environments.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string envId)
        {
            if (string.IsNullOrWhiteSpace(envId)) return;
            if (!visited.Add(envId)) return;

            if (byId.TryGetValue(envId, out var env))
            {
                ordered.Add(env);
            }

            if (promotionGraph.TryGetValue(envId, out var destinations))
            {
                foreach (var dest in destinations)
                {
                    Visit(dest);
                }
            }
        }

        foreach (var root in roots)
        {
            Visit(root);
        }

        // Append any remaining Flight Plan environments not in promotion chains (preserve YAML order).
        foreach (var env in environments)
        {
            if (string.IsNullOrWhiteSpace(env.Id)) continue;
            if (visited.Contains(env.Id)) continue;

            Visit(env.Id);
        }

        return ordered;
    }
}
