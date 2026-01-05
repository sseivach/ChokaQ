using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Dashboard.Hubs;

/// <summary>
/// The SignalR Hub responsible for real-time communication between the ChokaQ backend 
/// and the Dashboard UI.
/// <para>
/// Currently acts as a simple broadcaster. In the future, it can accept commands 
/// from the UI (e.g., CancelJob, RetryJob).
/// </para>
/// </summary>
public class ChokaQHub : Hub
{
    // Implementation intentionally left empty. 
    // We use IHubContext<ChokaQHub> in the backend to push notifications.
}