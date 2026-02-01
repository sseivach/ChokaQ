namespace ChokaQ.Abstractions.Jobs
{
    /// <summary>
    /// DTO to hold registration info.
    /// Made public so ChokaQ.Core can access the types during DI registration.
    /// </summary>
    public record JobRegistration(string Key, Type JobType, Type HandlerType);
}
