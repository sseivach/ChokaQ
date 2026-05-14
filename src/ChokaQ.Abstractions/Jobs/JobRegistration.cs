namespace ChokaQ.Abstractions.Jobs
{
    /// <summary>
    /// DTO to hold registration info.
    /// </summary>
    internal record JobRegistration(string Key, Type JobType, Type HandlerType);
}
