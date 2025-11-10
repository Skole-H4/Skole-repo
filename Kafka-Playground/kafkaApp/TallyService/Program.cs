// -------------------------------------------------------------------------------------------------
// TallyService entry point
//
// Responsibilities:
// 1. Bind and validate Kafka options required for streaming aggregation.
// 2. Register the schema registry client (JSON) for envelope and tally serialization.
// 3. Register the city catalog used to map city topic names to display metadata.
// 4. Seed required Kafka topics on startup if they do not already exist.
// 5. Start the StreamTallyHostedService which performs real time aggregation using Streamiz.
//
// Notes:
// - Top-level statements keep the bootstrap concise. All service registration is explicit.
// - No abbreviations are used in variables to maintain clarity for new readers.
// - Validation of KafkaOptions happens at startup to fail fast if configuration is invalid.
// -------------------------------------------------------------------------------------------------
#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task in a library.
using Confluent.SchemaRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TallyService.Abstractions;
using TallyService.Configuration;
using TallyService.HostedServices;
using TallyService.Services;

// Build the generic host (non-HTTP background service application)
var builder = Host.CreateApplicationBuilder(args);

// Bind Kafka configuration section and enable validation on start.
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection("Kafka"))
    .ValidateOnStart();

// Provide the validator and a direct resolved instance of KafkaOptions for simpler DI usage.
builder.Services.AddSingleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>();
builder.Services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value);

// Schema Registry client used by Streamiz custom SerDes implementations.
builder.Services.AddSingleton<ISchemaRegistryClient>(serviceProvider =>
{
    var kafkaOptions = serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value;
    return new CachedSchemaRegistryClient(new SchemaRegistryConfig
    {
        Url = kafkaOptions.SchemaRegistryUrl
    });
});

// City metadata catalog shared with the stream processor for mapping city topic fragments.
builder.Services.AddSingleton<ICityCatalog, CityCatalog>();

// Hosted services: topic seeding (idempotent) and continuous tally stream.
builder.Services.AddHostedService<KafkaTopicSeeder>();
builder.Services.AddHostedService<StreamTallyHostedService>();

// Build and run the host until shutdown.
var host = builder.Build();
await host.RunAsync();
