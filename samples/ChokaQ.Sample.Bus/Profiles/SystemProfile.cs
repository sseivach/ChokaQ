using ChokaQ.Abstractions.Jobs;
using ChokaQ.Sample.Bus.Jobs;

namespace ChokaQ.Sample.Bus.Profiles;

public class SystemProfile : ChokaQJobProfile
{
    public SystemProfile()
    {
        CreateJob<HealthCheckJob, HealthCheckHandler>("sys_health_check");
        CreateJob<CleanupJob, CleanupJobHandler>("sys_cleanup");
    }
}