using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using WebApp.Models;

namespace WebApp.Services;

public sealed class CityAutoVoteController : IAsyncDisposable
{
    public const int MaxVotesPerMinute = 20_000;

    private readonly IProducer<string, VoteEnvelope> _producer;
    private readonly string[] _options;
    private readonly string _votesTopic;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _targetPerMinute;
    private int _actualPerMinute;
    private long _totalSent;
    private string? _lastError;
    private int _isRunning;

    public CityAutoVoteController(CityTopic topic, IProducer<string, VoteEnvelope> producer, IReadOnlyList<string> options, string votesTopic)
    {
        if (options is null || options.Count == 0)
        {
            throw new ArgumentException("At least one option is required", nameof(options));
        }

        Topic = topic;
        _producer = producer;
        _options = options.ToArray();
        _votesTopic = votesTopic ?? throw new ArgumentNullException(nameof(votesTopic));
    }

    public CityTopic Topic { get; }

    public event Action<CityAutoVoteController>? StatusChanged;

    public int TargetPerMinute => Volatile.Read(ref _targetPerMinute);
    public int ActualPerMinute => Volatile.Read(ref _actualPerMinute);
    public long TotalSent => Interlocked.Read(ref _totalSent);
    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;
    public string? LastError => Volatile.Read(ref _lastError);

    public void SetTargetRate(int perMinute)
    {
        var clamped = Math.Clamp(perMinute, 0, MaxVotesPerMinute);
        Interlocked.Exchange(ref _targetPerMinute, clamped);
        Notify();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cts.Token));
            Volatile.Write(ref _isRunning, 1);
        }

        Notify();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? worker;

        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var sampleTimer = Stopwatch.StartNew();
        var sampleCount = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var target = Math.Clamp(Volatile.Read(ref _targetPerMinute), 0, MaxVotesPerMinute);

                if (target <= 0)
                {
                    if (Interlocked.Exchange(ref _actualPerMinute, 0) != 0)
                    {
                        Notify();
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                    continue;
                }

                var option = _options[Random.Shared.Next(_options.Length)]?.Trim().ToUpperInvariant() ?? string.Empty;
                if (option.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                    continue;
                }

                var vote = new VoteEvent
                {
                    UserId = $"auto-{Topic.ZipCode}-{Random.Shared.Next(1, 1_000_000):D6}",
                    Option = option,
                    Timestamp = DateTimeOffset.UtcNow
                };

                try
                {
                    var envelope = new VoteEnvelope
                    {
                        Event = vote,
                        CityTopic = Topic.TopicName,
                        City = Topic.City,
                        ZipCode = Topic.ZipCode
                    };

                    await _producer.ProduceAsync(_votesTopic, new Message<string, VoteEnvelope>
                    {
                        Key = BuildMessageKey(Topic.ZipCode, option),
                        Value = envelope
                    }).ConfigureAwait(false);
                    Volatile.Write(ref _lastError, null);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError, ex.Message);
                    Notify();
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }

                Interlocked.Increment(ref _totalSent);
                sampleCount++;

                if (sampleTimer.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    var perMinute = (int)Math.Round(sampleCount * 60 / sampleTimer.Elapsed.TotalSeconds);
                    Interlocked.Exchange(ref _actualPerMinute, perMinute);
                    sampleCount = 0;
                    sampleTimer.Restart();
                    Notify();
                }

                var delayMs = Math.Max(1, 60000d / target);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token);
            }
        }
        finally
        {
            sampleTimer.Stop();
            Interlocked.Exchange(ref _actualPerMinute, 0);
            Volatile.Write(ref _isRunning, 0);
            Notify();
        }
    }

    private void Notify() => StatusChanged?.Invoke(this);

    private static string BuildMessageKey(int zipCode, string option)
        => string.Concat(zipCode.ToString(CultureInfo.InvariantCulture), '|', option);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
