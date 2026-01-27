using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities; // ВАЖНО: Используем Entities
using ChokaQ.Core.Concurrency;
using ChokaQ.Core.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Worker implementing the "Prefetching Consumer" pattern.
/// Decouples DB fetching latency from Job Execution throughput using an elastic concurrency limiter.
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly JobProcessor _processor; // Используем конкретный класс или обновленный интерфейс
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // FIX: Используем JobEntity вместо старого DTO
    private readonly Channel<JobEntity> _prefetchBuffer;

    // Encapsulates the logic for dynamic scaling.
    private readonly ElasticSemaphore _concurrencyLimiter;

    public int ActiveWorkers => _concurrencyLimiter.RunningCount;
    public int TotalWorkers => _concurrencyLimiter.Capacity;

    // Эти свойства лучше убрать, если Processor их не шарит публично, 
    // но для совместимости оставим (предполагая, что в Processor есть публичные свойства)
    public int MaxRetries { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 2;

    public SqlJobWorker(
        IJobStorage storage,
        JobProcessor processor, // Inject Core Processor
        ILogger<SqlJobWorker> logger,
        SqlJobStorageOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SqlJobStorageOptions();

        // Buffer limit 100
        _prefetchBuffer = Channel.CreateBounded<JobEntity>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });

        _concurrencyLimiter = new ElasticSemaphore(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker Starting. Strategy: Prefetch + ElasticSemaphore. Initial Capacity: {Capacity}",
            _concurrencyLimiter.Capacity);

        // 1. Supplier
        var fetcherTask = Task.Factory.StartNew(
            () => FetcherLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        // 2. Consumer
        var processorTask = Task.Factory.StartNew(
            () => ProcessorLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        await Task.WhenAll(fetcherTask, processorTask);
    }

    private async Task FetcherLoopAsync(CancellationToken ct)
    {
        var workerId = Environment.MachineName;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1. Ждем, пока в буфере (Channel) освободится место
                await _prefetchBuffer.Writer.WaitToWriteAsync(ct);

                // 2. Вычисляем, сколько задач можем взять
                int batchSize = Math.Min(20, 100 - _prefetchBuffer.Reader.Count);
                if (batchSize <= 0) batchSize = 1;

                // 3. Определяем очереди для прослушивания.
                // В идеале: получить из конфига или базы (GetQueuesAsync).
                // Для старта хардкодим "default", чтобы все заработало.
                var queues = new[] { "default" };

                // FIX: Добавил 'queues' третьим аргументом!
                var jobs = await _storage.FetchNextBatchAsync(workerId, batchSize, queues, ct);

                if (!jobs.Any())
                {
                    // Если пусто, спим чуть дольше, чтобы не долбить базу
                    await Task.Delay(_options.PollingInterval, ct);
                    continue;
                }

                // 4. Закидываем задачи в буфер для Процессора
                foreach (var job in jobs)
                {
                    await _prefetchBuffer.Writer.WriteAsync(job, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 Fetcher Loop crashed. Cooling down...");
                await Task.Delay(5000, ct);
            }
        }

        _logger.LogInformation("Fetcher Loop Stopped.");
    }

    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync(ct))
            {
                // Ждем семафор
                await _concurrencyLimiter.WaitAsync(ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // FIX: Больше не нужно вручную менять статус на Processing.
                        // FetchNextBatchAsync уже поставил статус "Fetched" и лок.
                        // JobProcessor сам пошлет уведомление в UI, что процесс пошел.

                        // Передаем ВЕСЬ объект задачи в процессор
                        await _processor.ProcessJobAsync(job, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Critical error in job execution wrapper for {JobId}", job.Id);
                    }
                    finally
                    {
                        _concurrencyLimiter.Release();
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // --- Management Implementation ---

    public void UpdateWorkerCount(int count)
    {
        _logger.LogInformation("Updating worker count to {Count}", count);
        _concurrencyLimiter.SetCapacity(count);
    }

    // Эти методы теперь проксируем напрямую в Storage или Processor,
    // так как Worker сам по себе - просто молотилка.

    public async Task CancelJobAsync(string jobId)
    {
        // Cancel logic is usually: Update DB to Cancelled + Cancel CancellationToken
        // _processor.Cancel(jobId); // Если процессор поддерживает отмену на лету

        // Удаляем из базы (Purge) или ставим статус Cancelled
        await _storage.PurgeJobAsync(jobId);
    }

    public async Task RestartJobAsync(string jobId)
    {
        // Воскрешаем через Storage
        await _storage.ResurrectJobAsync(jobId);
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        // Обновляем приоритет (если метод есть в интерфейсе Storage)
        // await _storage.UpdateJobPriorityAsync(jobId, priority);
        // Если метода нет, можно оставить пустым или добавить в IJobStorage
    }

    public override void Dispose()
    {
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }
}