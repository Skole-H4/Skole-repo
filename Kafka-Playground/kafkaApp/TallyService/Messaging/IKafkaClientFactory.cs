namespace TallyService.Messaging;

using Confluent.Kafka;
using TallyService.Models;

public interface IKafkaClientFactory
{
    IConsumer<string, VoteEnvelope> CreateVoteConsumer();

    IProducer<string, VoteTotal> CreateTallyProducer();

    IConsumer<string, VoteTotal> CreateTotalsConsumer(string groupId);
}
