using System;
using System.Collections.Generic;
using System.Linq;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WebApp.Components;
using WebApp.Configuration;
using WebApp.Models;
using WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaOptions>>().Value);

builder.Services.AddHttpClient();

// Shared metadata and live in-memory state keyed by vote option with change notifications
builder.Services.AddSingleton<PartyCatalog>();
builder.Services.AddSingleton<CityCatalog>();
builder.Services.AddSingleton<VoteTotalsStore>();
builder.Services.AddSingleton<CityVoteStore>();

builder.Services.AddSingleton<ISchemaRegistryClient>(sp =>
{
    var options = sp.GetRequiredService<KafkaOptions>();
    return new CachedSchemaRegistryClient(new SchemaRegistryConfig
    {
        Url = options.SchemaRegistryUrl
    });
});

builder.Services.AddSingleton<IProducer<string, VoteEnvelope>>(sp =>
{
    var options = sp.GetRequiredService<KafkaOptions>();
    var producerConfig = new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = 5,
        BatchSize = 64_000
    };

    var schemaClient = sp.GetRequiredService<ISchemaRegistryClient>();

    return new ProducerBuilder<string, VoteEnvelope>(producerConfig)
        .SetValueSerializer(new JsonSerializer<VoteEnvelope>(schemaClient, new JsonSerializerConfig
        {
            AutoRegisterSchemas = true
        }))
        .Build();
});

builder.Services.AddSingleton<CityAutoVoteManager>();
builder.Services.AddHostedService<KafkaTopicSeeder>();
builder.Services.AddHostedService<TotalsConsumer>();
builder.Services.AddHostedService<CityVotesConsumer>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
    });
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/totals", (VoteTotalsStore store) => Results.Json(store.GetSnapshot()));
app.MapGet("/api/cities", (CityVoteStore store) => Results.Json(store.GetSnapshot()));

app.MapPost("/api/vote", async (VoteRequest request, CityCatalog catalog, PartyCatalog partyCatalog, IProducer<string, VoteEnvelope> producer, KafkaOptions kafkaOptions) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Option))
    {
        return Results.BadRequest("UserId and Option are required.");
    }

    var option = request.Option.Trim().ToUpperInvariant();
    if (!partyCatalog.TryGetByLetter(option, out _))
    {
        return Results.BadRequest($"Unknown option '{option}'.");
    }

    var vote = new VoteEvent
    {
        UserId = request.UserId,
        Option = option,
        Timestamp = DateTimeOffset.UtcNow
    };

    var targets = ResolveTargets(request.TargetTopics, catalog);
    var results = new List<object>(targets.Count == 0 ? 1 : targets.Count);

    if (targets.Count == 0)
    {
        var envelope = CreateEnvelope(vote, null);
        var delivery = await producer.ProduceAsync(kafkaOptions.VotesTopic, new Message<string, VoteEnvelope>
        {
            Key = BuildMessageKey(null, option),
            Value = envelope
        });

        results.Add(new
        {
            topic = kafkaOptions.VotesTopic,
            partition = delivery.Partition.Value,
            offset = delivery.Offset.Value,
            city = envelope.City,
            zip = envelope.ZipCode
        });
    }
    else
    {
        foreach (var city in targets)
        {
            var envelope = CreateEnvelope(vote, city);
            var delivery = await producer.ProduceAsync(kafkaOptions.VotesTopic, new Message<string, VoteEnvelope>
            {
                Key = BuildMessageKey(city.ZipCode, option),
                Value = envelope
            });

            results.Add(new
            {
                topic = kafkaOptions.VotesTopic,
                partition = delivery.Partition.Value,
                offset = delivery.Offset.Value,
                city = envelope.City,
                zip = envelope.ZipCode
            });
        }
    }

    return Results.Ok(new
    {
        status = "queued",
        deliveries = results
    });
});

app.MapPost("/api/cities/start", (CityControlRequest request, CityAutoVoteManager manager) =>
{
    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    manager.Start(controllers, request.Rate);
    return Results.Ok(new
    {
        started = controllers.Count,
        rate = request.Rate
    });
});

app.MapPost("/api/cities/stop", async (CityControlRequest request, CityAutoVoteManager manager) =>
{
    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    await manager.StopAsync(controllers);
    return Results.Ok(new
    {
        stopped = controllers.Count
    });
});

app.MapPost("/api/cities/rate", (CityControlRequest request, CityAutoVoteManager manager) =>
{
    if (request.Rate is null)
    {
        return Results.BadRequest("Rate is required.");
    }

    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    manager.SetRate(controllers, request.Rate.Value);
    return Results.Ok(new
    {
        updated = controllers.Count,
        rate = request.Rate.Value
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static List<CityTopic> ResolveTargets(IEnumerable<string>? requested, CityCatalog catalog)
{
    var results = new HashSet<CityTopic>(CityTopicComparer.Instance);

    if (requested is not null)
    {
        foreach (var entry in requested)
        {
            var value = entry?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (catalog.TryResolve(value, out var city) && city is not null)
            {
                results.Add(city);
            }
        }
    }

    return results.ToList();
}

static string BuildMessageKey(int? zipCode, string option)
{
    return zipCode.HasValue
        ? string.Concat(zipCode.Value, '|', option)
        : string.Concat("GLOBAL|", option);
}

static VoteEnvelope CreateEnvelope(VoteEvent vote, CityTopic? city)
{
    return new VoteEnvelope
    {
        Event = vote,
        CityTopic = city?.TopicName,
        City = city?.City,
        ZipCode = city?.ZipCode
    };
}

sealed class CityTopicComparer : IEqualityComparer<CityTopic>
{
    public static CityTopicComparer Instance { get; } = new();

    public bool Equals(CityTopic? x, CityTopic? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.TopicName, y.TopicName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(CityTopic obj)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TopicName);
}

sealed class TotalsConsumer : BackgroundService
{
    private readonly VoteTotalsStore _store;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly KafkaOptions _options;

    public TotalsConsumer(VoteTotalsStore store, ISchemaRegistryClient schemaRegistryClient, KafkaOptions options)
    {
        _store = store;
        _schemaRegistryClient = schemaRegistryClient;
        _options = options;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Offload loop to long-running task so BackgroundService can manage cancellation cleanly.
        return Task.Run(() =>
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = _options.BootstrapServers,
                GroupId = _options.UiTotalsGroupId ?? "webapp-totals-ui",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            };

            config.Set("socket.keepalive.enable", "true");
            config.Set("debug", "broker,protocol,security");
            config.Set("broker.address.family", "v4");

            using var consumer = new ConsumerBuilder<string, VoteTotal>(config)
                .SetValueDeserializer(new JsonDeserializer<VoteTotal>(_schemaRegistryClient).AsSyncOverAsync())
                .Build();

            consumer.Subscribe(_options.TotalsTopic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = consumer.Consume(stoppingToken);
                    if (result?.Message?.Value is { } total && !string.IsNullOrWhiteSpace(total.Option))
                    {
                        _store.SetTotal(total.Option, total.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch
                {
                    // swallow shutdown errors
                }
            }
        }, stoppingToken);
    }
}

sealed class CityVotesConsumer : BackgroundService
{
    private readonly CityVoteStore _store;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly KafkaOptions _options;

    public CityVotesConsumer(CityVoteStore store, ISchemaRegistryClient schemaRegistryClient, KafkaOptions options)
    {
        _store = store;
        _schemaRegistryClient = schemaRegistryClient;
        _options = options;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = _options.BootstrapServers,
                GroupId = _options.UiCityGroupId ?? "webapp-city-metrics",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            };

            config.Set("socket.keepalive.enable", "true");
            config.Set("debug", "broker,protocol,security");
            config.Set("broker.address.family", "v4");

            using var consumer = new ConsumerBuilder<string, VoteTotal>(config)
                .SetValueDeserializer(new JsonDeserializer<VoteTotal>(_schemaRegistryClient).AsSyncOverAsync())
                .Build();

            consumer.Subscribe(_options.VotesByCityTopic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = consumer.Consume(stoppingToken);
                    if (result?.Message?.Value is { } total && !string.IsNullOrWhiteSpace(total.Option))
                    {
                        _store.SetCityVote(total);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch
                {
                    // swallow shutdown errors
                }
            }
        }, stoppingToken);
    }
}
