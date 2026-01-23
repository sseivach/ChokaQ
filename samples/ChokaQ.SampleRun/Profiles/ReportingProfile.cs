using ChokaQ.Abstractions;
using ChokaQ.SampleRun.Jobs;

namespace ChokaQ.SampleRun.Profiles;

public class ReportingProfile : ChokaQJobProfile
{
    public ReportingProfile()
    {
        CreateJob<EmployeeReportJob, ReportingHandler<EmployeeReportJob>>("rep_employee");
        CreateJob<SalesReportJob, ReportingHandler<SalesReportJob>>("rep_sales");
        CreateJob<ExpenseReportJob, ReportingHandler<ExpenseReportJob>>("rep_expenses");
    }
}