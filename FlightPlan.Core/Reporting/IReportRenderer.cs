namespace FlightPlan.Reporting;

public interface IReportDocumentRenderer
{
    string Render(ReportDocument document);
}