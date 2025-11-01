using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamiz.Kafka.Net;
using Streamiz.Kafka.Net.Stream;
using Streamiz.Kafka.Net.SerDes;
using Streamiz.Kafka.Net.State;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using TallyService.Configuration;

namespace TallyService.HostedServices;

/// <summary>
/// Minimal diagnostic hosted service: single aggregation Count() to isolate internal topic creation failure.
/// </summary>
public sealed class MiniStreamHostedService : IHostedService
{
    private readonly KafkaOptions _options;
    private readonly ILogger<MiniStreamHostedService> _logger;
    private KafkaStream? _stream;
    private Task? _task;
    private string _appId = string.Empty;

    public MiniStreamHostedService(KafkaOptions options, ILogger<MiniStreamHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return Task.CompletedTask;
        }
        _appId = _options.TallyGroupId + "-mini-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var builder = new StreamBuilder();
        // Topology: votes topic -> filter non-empty -> key=upper(value) -> groupByKey -> count -> map -> sink
        var source = builder.Stream<string, string>(_options.VotesTopic, new StringSerDes(), new StringSerDes());
        var counts = source
            .Filter((k, v, _) => !string.IsNullOrWhiteSpace(v))
            .SelectKey<string>((_, v, _) => v!.Trim().ToUpperInvariant())
            .GroupByKey()
            .Count("mini-counts")
            .ToStream();
        // Wrap primitive long in CountRecord for JSON serialization without schema evolution complexity.
        counts.MapValues((v, _) => new CountRecord { Count = v })
              .To(_options.TotalsTopic, new StringSerDes(), new JsonSerDes<CountRecord>());

        var config = new StreamConfig
        {
            ApplicationId = _appId,
            BootstrapServers = _options.BootstrapServers,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            StateDir = System.IO.Path.Combine(AppContext.BaseDirectory, "mini-state", _appId),
            Guarantee = ProcessingGuarantee.AT_LEAST_ONCE,
            ReplicationFactor = _options.DefaultReplicationFactor,
            CommitIntervalMs = StreamConfig.EOS_DEFAULT_COMMIT_INTERVAL_MS,
            NumStreamThreads = 1,
            DefaultKeySerDes = new StringSerDes(),
            DefaultValueSerDes = new StringSerDes()
        };
    // Avoid implicit topic auto-creation; rely on external provisioning.
    config.AllowAutoCreateTopics = false;
        config.InnerExceptionHandler = ex =>
        {
            // Diagnostic mode: treat internal topic existence conflicts as benign.
            if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Mini stream ignoring already-exists internal topic error: {Message}", ex.Message);
                return Streamiz.Kafka.Net.ExceptionHandlerResponse.CONTINUE;
            }
            // Continue on any exception; failures will appear in logs for manual inspection.
            return Streamiz.Kafka.Net.ExceptionHandlerResponse.CONTINUE;
        };

        LogInternalTopics(_appId);
        var topology = builder.Build();
        _stream = new KafkaStream(topology, config);
    _task = _stream.StartAsync(cancellationToken);
        _logger.LogInformation("Mini stream start requested with appId {AppId}", _appId);
        _task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception?.GetBaseException(), "Mini stream faulted");
            }
            else if (t.IsCanceled)
            {
                _logger.LogWarning("Mini stream cancelled");
            }
            else
            {
                _logger.LogInformation("Mini stream completed");
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }
        try
        {
            _stream.Dispose();
            if (_task is not null)
            {
                await Task.WhenAny(_task, Task.Delay(5000, cancellationToken));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping mini stream");
        }
    }

    private void LogInternalTopics(string appId)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
            var meta = admin.GetMetadata(TimeSpan.FromSeconds(5));
            var prefix = appId + "-";
            var list = new List<string>();
            foreach (var t in meta.Topics)
            {
                if (t.Error.Code == ErrorCode.NoError && t.Topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(t.Topic);
                }
            }
            if (list.Count == 0)
            {
                _logger.LogInformation("Mini stream: no existing internal topics for {AppId}", appId);
            }
            else
            {
                _logger.LogWarning("Mini stream: existing internal topics for {AppId}: {Topics}", appId, string.Join(", ", list));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mini stream: failed to log internal topics");
        }
    }
}

public sealed class CountRecord
{
    public long Count { get; set; }
}
