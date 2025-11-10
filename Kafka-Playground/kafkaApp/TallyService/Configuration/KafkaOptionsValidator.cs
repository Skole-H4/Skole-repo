namespace TallyService.Configuration;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

public sealed class KafkaOptionsValidator : IValidateOptions<KafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Kafka options cannot be null.");
        }

        var failures = new List<string>();

        ValidateRequired(options.BootstrapServers, nameof(options.BootstrapServers));
        ValidateRequired(options.SchemaRegistryUrl, nameof(options.SchemaRegistryUrl));
        ValidateRequired(options.VotesTopic, nameof(options.VotesTopic));
        ValidateRequired(options.TotalsTopic, nameof(options.TotalsTopic));
        ValidateRequired(options.VotesByCityTopic, nameof(options.VotesByCityTopic));
        ValidateRequired(options.TallyGroupId, nameof(options.TallyGroupId));
        ValidateRequired(options.TallyTransactionalId, nameof(options.TallyTransactionalId));

        if (options.DefaultPartitions <= 0)
        {
            failures.Add("DefaultPartitions must be greater than zero.");
        }

        if (options.DefaultReplicationFactor <= 0)
        {
            failures.Add("DefaultReplicationFactor must be greater than zero.");
        }

        if (options.StartupGuardDelaySeconds < 0)
        {
            failures.Add("StartupGuardDelaySeconds cannot be negative.");
        }
        if (options.MaxStreamStartupRetries < 0)
        {
            failures.Add("MaxStreamStartupRetries cannot be negative.");
        }
        if (options.InternalTopicDeletionTimeoutSeconds <= 0)
        {
            failures.Add("InternalTopicDeletionTimeoutSeconds must be greater than zero.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;

        void ValidateRequired(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"{fieldName} is required.");
            }
        }
    }
}
