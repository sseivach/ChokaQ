using ChokaQ.Abstractions;
using ChokaQ.SampleRun.Jobs;

namespace ChokaQ.SampleRun.Profiles;

public class SystemProfile : ChokaQJobProfile
{
    public SystemProfile()
    {
        CreateJob<HealthCheckJob, HealthCheckHandler>("sys_health_check");
        CreateJob<CleanupJob, CleanupJobHandler>("sys_cleanup");
    }
}