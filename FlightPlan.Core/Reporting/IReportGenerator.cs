using FlightPlan.Models;

namespace FlightPlan.Reporting;

public interface IReportGenerator
{
    ReportDocument Generate(CompiledFlightPlan plan);
    
    ReportDocument Generate(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        // Default implementation for backward compatibility
        return Generate(plan);
    }
}

public sealed class ReportGenerationOptions
{
    /// <summary>
    /// When true, show empty sections and rows with "Not modeled yet" placeholder.
    /// When false, hide empty sections and rows entirely.
    /// </summary>
    public bool ShowEmptySections { get; init; } = false;
}
