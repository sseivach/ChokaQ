using ChokaQ.Abstractions;
using ChokaQ.SampleRun.Jobs;

namespace ChokaQ.SampleRun.Profiles;

public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<EmailJob, EmailJobHandler>("mail_email_v1");
        CreateJob<SmsJob, SmsJobHandler>("mail_sms_v1");
    }
}