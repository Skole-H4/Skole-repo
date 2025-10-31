namespace TallyService.Messaging;

using System;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using TallyService.Configuration;
using TallyService.Models;

public sealed class KafkaClientFactory : IKafkaClientFactory
{
    private readonly KafkaOptions _options;
    private readonly ISchemaRegistryClient _schemaRegistryClient;

    public KafkaClientFactory(KafkaOptions options, ISchemaRegistryClient schemaRegistryClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _schemaRegistryClient = schemaRegistryClient ?? throw new ArgumentNullException(nameof(schemaRegistryClient));
    }

    public IConsumer<string, VoteEvent> CreateVoteConsumer()
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.TallyGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            IsolationLevel = IsolationLevel.ReadCommitted
        };

        consumerConfig.Set("socket.keepalive.enable", "true");
        consumerConfig.Set("debug", "broker,protocol,security");
        consumerConfig.Set("broker.address.family", "v4");

        return new ConsumerBuilder<string, VoteEvent>(consumerConfig)
            .SetValueDeserializer(new JsonDeserializer<VoteEvent>(_schemaRegistryClient).AsSyncOverAsync())
            .Build();
    }

    public IProducer<string, VoteTotal> CreateTallyProducer()
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            TransactionalId = _options.TallyTransactionalId,
            LingerMs = 5,
            BatchSize = 64_000
        };

        producerConfig.Set("socket.keepalive.enable", "true");
        producerConfig.Set("debug", "broker,protocol,security");
        producerConfig.Set("broker.address.family", "v4");

        var serializerConfig = new JsonSerializerConfig
        {
            AutoRegisterSchemas = true
        };

        return new ProducerBuilder<string, VoteTotal>(producerConfig)
            .SetValueSerializer(new JsonSerializer<VoteTotal>(_schemaRegistryClient, serializerConfig).AsSyncOverAsync())
            .Build();
    }

    public IConsumer<string, VoteTotal> CreateTotalsConsumer(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("Group identifier is required", nameof(groupId));
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            IsolationLevel = IsolationLevel.ReadCommitted
        };

        consumerConfig.Set("socket.keepalive.enable", "true");
        consumerConfig.Set("broker.address.family", "v4");

        return new ConsumerBuilder<string, VoteTotal>(consumerConfig)
            .SetValueDeserializer(new JsonDeserializer<VoteTotal>(_schemaRegistryClient).AsSyncOverAsync())
            .Build();
    }
}
