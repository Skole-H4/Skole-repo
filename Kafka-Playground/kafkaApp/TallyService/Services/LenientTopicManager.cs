using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Confluent.Kafka.Admin;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Streamiz.Kafka.Net;

namespace TallyService.Services;

/// <summary>
/// Lenient wrapper around Streamiz DefaultTopicManager using reflection only (no direct reference to internal interfaces).
/// Treats TopicAlreadyExists errors during ApplyAsync as success to allow reusing application.id without shutdown.
/// </summary>
internal sealed class LenientTopicManager
{
    private readonly object _inner;
    private readonly ILogger<LenientTopicManager> _logger;
    private readonly MethodInfo? _applyAsync;
    private readonly MethodInfo? _deleteAsync;

    public LenientTopicManager(StreamConfig config, ILogger<LenientTopicManager> logger)
    {
        _logger = logger;
        var processorsAssembly = typeof(KafkaStream).Assembly; // Streamiz assembly
        var dtmType = processorsAssembly.GetType("Streamiz.Kafka.Net.Processors.DefaultTopicManager");
        if (dtmType == null)
        {
            throw new InvalidOperationException("DefaultTopicManager type not found via reflection.");
        }
        object? instance = null;
        var adminConfig = new AdminClientConfig { BootstrapServers = config.BootstrapServers };
        var admin = new AdminClientBuilder(adminConfig).Build();
        // Try (IAdminClient, StreamConfig) first
        var ctor = dtmType.GetConstructor(new[] { typeof(IAdminClient), typeof(StreamConfig) });
        if (ctor != null)
        {
            instance = ctor.Invoke(new object[] { admin, config });
        }
        else
        {
            ctor = dtmType.GetConstructor(Type.EmptyTypes);
            if (ctor != null)
            {
                instance = ctor.Invoke(Array.Empty<object>());
            }
        }
        _inner = instance ?? throw new InvalidOperationException("Unable to construct default topic manager instance.");
        _applyAsync = dtmType.GetMethod("ApplyAsync", BindingFlags.Public | BindingFlags.Instance);
        _deleteAsync = dtmType.GetMethod("DeleteAsync", BindingFlags.Public | BindingFlags.Instance);
        if (_applyAsync == null)
        {
            throw new InvalidOperationException("ApplyAsync not found on DefaultTopicManager.");
        }
        _logger.LogDebug("LenientTopicManager initialized; reflection hooks acquired.");
    }

    public async Task ApplyAsync(int topologyId, IDictionary<string, object> topics)
    {
        try
        {
            var taskObj = _applyAsync!.Invoke(_inner, new object[] { topologyId, topics });
            if (taskObj is Task t)
            {
                await t.ConfigureAwait(false);
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            if (tie.InnerException is Streamiz.Kafka.Net.Errors.StreamsException se && se.InnerException is CreateTopicsException cte)
            {
                var nonBenign = cte.Results.Where(r => r.Error.Code != ErrorCode.TopicAlreadyExists).ToList();
                if (nonBenign.Count == 0)
                {
                    _logger.LogWarning("Ignoring TopicAlreadyExists for internal topics: {Topics}", string.Join(", ", cte.Results.Select(r => r.Topic)));
                    return;
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            throw new Streamiz.Kafka.Net.Errors.StreamsException("LenientTopicManager.ApplyAsync failed", ex);
        }
    }

    public Task DeleteAsync(IEnumerable<string> topics)
    {
        if (_deleteAsync == null)
        {
            return Task.CompletedTask; // deletion not needed
        }
        var result = _deleteAsync.Invoke(_inner, new object[] { topics });
        return result as Task ?? Task.CompletedTask;
    }
}

