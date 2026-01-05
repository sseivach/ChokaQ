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
    /// Accepts a list of specific payloads to process.
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> CreateBatch([FromBody] List<string> payloads)
    {
        // Validation: Don't accept empty lists or massive payloads in one go
        if (payloads == null || payloads.Count == 0)
        {
            return BadRequest("Payload list cannot be empty.");
        }

        if (payloads.Count > 1000)
        {
            return BadRequest("Batch size limited to 1000 items.");
        }

        var jobIds = new List<string>(payloads.Count);

        foreach (var payload in payloads)
        {
            // We map the incoming raw data (string) to our Job DTO
            var job = new PrintMessageJob(payload);

            await _queue.EnqueueAsync(job);
            jobIds.Add(job.Id);
        }

        return Ok(new
        {
            Message = $"Successfully queued {payloads.Count} jobs.",
            JobIds = jobIds
        });
    }

    /// <summary>
    /// Enqueues a single job.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BadRequest("Payload cannot be empty.");
        }

        var job = new PrintMessageJob(payload);
        await _queue.EnqueueAsync(job);

        return Accepted(new
        {
            JobId = job.Id,
            Status = "Queued"
        });
    }
}