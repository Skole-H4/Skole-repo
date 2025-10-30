namespace TallyService.Messaging;

using Confluent.Kafka;
using TallyService.Models;

public interface IKafkaClientFactory
{
    IConsumer<string, VoteEvent> CreateVoteConsumer();

    IProducer<string, VoteTotal> CreateTallyProducer();
}
