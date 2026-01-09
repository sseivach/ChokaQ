using ChokaQ.Abstractions;
using ChokaQ.SampleApp.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace ChokaQ.SampleApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IChokaQQueue _queue;

    public JobsController(IChokaQQueue queue)
    {
        _queue = queue;
    }

    /// <summary>
    /// DTO for batch job creation request.
    /// </summary>
    public class BatchRequest
    {
        public List<string> Payloads { get; set; } = new();
        public int Priority { get; set; } = 10;
        public string CreatedBy { get; set; } = "System";
    }

    /// <summary>
    /// Accepts a batch of jobs with metadata (Priority, User).
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> CreateBatch([FromBody] BatchRequest request)
    {
        // Validation
        if (request.Payloads == null || request.Payloads.Count == 0)
        {
            return BadRequest("Payload list cannot be empty.");
        }

        if (request.Payloads.Count > 1000)
        {
            return BadRequest("Batch size limited to 1000 items.");
        }

        var jobIds = new List<string>(request.Payloads.Count);

        // Define tags based on priority for easier filtering later
        var tags = $"Batch,{request.CreatedBy},Pri:{request.Priority}";

        foreach (var payload in request.Payloads)
        {
            var job = new PrintMessageJob(payload);

            // Pass the metadata (Priority, CreatedBy) to the queue
            await _queue.EnqueueAsync(
                job,
                priority: request.Priority,
                createdBy: request.CreatedBy,
                tags: tags
            );

            jobIds.Add(job.Id);
        }

        return Ok(new
        {
            Message = $"Successfully queued {request.Payloads.Count} jobs (Pri: {request.Priority}).",
            JobIds = jobIds
        });
    }
}