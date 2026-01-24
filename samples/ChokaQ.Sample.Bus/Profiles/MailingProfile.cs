using ChokaQ.Abstractions;
using ChokaQ.Sample.Bus.Jobs;

namespace ChokaQ.Sample.Bus.Profiles;

public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<EmailJob, EmailJobHandler>("mail_email_v1");
        CreateJob<SmsJob, SmsJobHandler>("mail_sms_v1");
    }
}