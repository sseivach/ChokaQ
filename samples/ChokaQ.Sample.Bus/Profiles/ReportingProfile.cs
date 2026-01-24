using ChokaQ.Abstractions;
using ChokaQ.Sample.Bus.Jobs;

namespace ChokaQ.Sample.Bus.Profiles;

public class ReportingProfile : ChokaQJobProfile
{
    public ReportingProfile()
    {
        CreateJob<EmployeeReportJob, ReportingHandler<EmployeeReportJob>>("rep_employee");
        CreateJob<SalesReportJob, ReportingHandler<SalesReportJob>>("rep_sales");
        CreateJob<ExpenseReportJob, ReportingHandler<ExpenseReportJob>>("rep_expenses");
    }
}