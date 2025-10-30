#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task in a library.
using System.Collections.Generic;
using System.Linq;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;
using TallyService.Configuration;
using TallyService.Models;
using TallyService.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaOptions>>().Value);
builder.Services.AddSingleton<CityCatalog>();
builder.Services.AddHostedService<TallyWorker>();

var host = builder.Build();
await host.RunAsync();

sealed class TallyWorker : BackgroundService
{
    private readonly KafkaOptions _options;
    private readonly CityCatalog _catalog;
	private readonly Dictionary<string, int> _globalCounts = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Dictionary<string, int>> _cityCounts = new(StringComparer.OrdinalIgnoreCase);

    public TallyWorker(IOptions<KafkaOptions> options, CityCatalog catalog)
    {
        _options = options.Value;
        _catalog = catalog;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		return Task.Run(() =>
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

			using var schemaRegistry = new CachedSchemaRegistryClient(new SchemaRegistryConfig
			{
				Url = _options.SchemaRegistryUrl
			});

			using var consumer = new ConsumerBuilder<string, VoteEvent>(consumerConfig)
				.SetValueDeserializer(new JsonDeserializer<VoteEvent>(schemaRegistry).AsSyncOverAsync())
				.Build();

			using var producer = new ProducerBuilder<string, VoteTotal>(producerConfig)
				.SetValueSerializer(new JsonSerializer<VoteTotal>(schemaRegistry, new JsonSerializerConfig
				{
					AutoRegisterSchemas = true
				}).AsSyncOverAsync())
				.Build();

			producer.InitTransactions(TimeSpan.FromSeconds(10));

			var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				_options.VotesTopic
			};
			foreach (var city in _catalog.Cities)
			{
				topics.Add(city.TopicName);
			}
			consumer.Subscribe(topics.ToList());

			try
			{
				while (!stoppingToken.IsCancellationRequested)
				{
					var record = consumer.Consume(TimeSpan.FromMilliseconds(500));
					if (record is null)
					{
						continue;
					}

					var vote = record.Message?.Value;
					if (vote is null || string.IsNullOrWhiteSpace(vote.Option))
					{
						// Skip bad payloads but advance the consumer to avoid replays.
						producer.BeginTransaction();
						producer.SendOffsetsToTransaction(
							new[] { new TopicPartitionOffset(record.TopicPartition, record.Offset + 1) },
							consumer.ConsumerGroupMetadata,
							TimeSpan.FromSeconds(10));
						producer.CommitTransaction();
						continue;
					}

					var option = vote.Option.Trim().ToUpperInvariant();
					if (option.Length == 0)
					{
						producer.BeginTransaction();
						producer.SendOffsetsToTransaction(
							new[] { new TopicPartitionOffset(record.TopicPartition, record.Offset + 1) },
							consumer.ConsumerGroupMetadata,
							TimeSpan.FromSeconds(10));
						producer.CommitTransaction();
						continue;
					}

					var globalCount = IncrementCount(_globalCounts, option);

					CityTopic? cityInfo = null;
					VoteTotal? cityTotal = null;
					if (_catalog.TryGetByTopic(record.Topic, out var resolved) && resolved is not null)
					{
						cityInfo = resolved;
						var perCity = GetCityBucket(cityInfo.TopicName);
						var cityCount = IncrementCount(perCity, option);
						cityTotal = new VoteTotal
						{
							Option = option,
							Count = cityCount,
							UpdatedAt = DateTimeOffset.UtcNow,
							City = cityInfo.City,
							ZipCode = cityInfo.ZipCode
						};
					}

					var globalTotal = new VoteTotal
					{
						Option = option,
						Count = globalCount,
						UpdatedAt = DateTimeOffset.UtcNow
					};

					producer.BeginTransaction();
					producer.Produce(_options.TotalsTopic, new Message<string, VoteTotal>
					{
						Key = option,
						Value = globalTotal
					});

					if (cityInfo is not null && cityTotal is not null)
					{
						var cityKey = $"{cityInfo.TopicName}:{option}";
						producer.Produce(_options.VotesByCityTopic, new Message<string, VoteTotal>
						{
							Key = cityKey,
							Value = cityTotal
						});
					}

					producer.SendOffsetsToTransaction(
						new[] { new TopicPartitionOffset(record.TopicPartition, record.Offset + 1) },
						consumer.ConsumerGroupMetadata,
						TimeSpan.FromSeconds(10));

					producer.CommitTransaction();
				}
			}
			catch (OperationCanceledException)
			{
				// expected on shutdown
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
				try
				{
					producer.AbortTransaction();
				}
				catch
				{
					// swallow abort errors during shutdown
				}
			}
			finally
			{
				try
				{
					consumer.Close();
				}
				catch
				{
					// ignore close errors
				}
			}
		}, stoppingToken);
	}

	private static int IncrementCount(Dictionary<string, int> store, string option)
	{
		if (store.TryGetValue(option, out var current))
		{
			current++;
		}
		else
		{
			current = 1;
		}

		store[option] = current;
		return current;
	}

	private Dictionary<string, int> GetCityBucket(string cityKey)
	{
		if (!_cityCounts.TryGetValue(cityKey, out var bucket))
		{
			bucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			_cityCounts[cityKey] = bucket;
		}

		return bucket;
	}
}
