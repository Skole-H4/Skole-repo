#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task in a library.
using Confluent.SchemaRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TallyService.Abstractions;
using TallyService.Configuration;
using TallyService.HostedServices;
using TallyService.Services;
using TallyService.Messaging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection("Kafka"))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaOptions>>().Value);

builder.Services.AddSingleton<ISchemaRegistryClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    return new CachedSchemaRegistryClient(new SchemaRegistryConfig
    {
        Url = options.SchemaRegistryUrl
    });
});

builder.Services.AddSingleton<ICityCatalog, CityCatalog>();
builder.Services.AddSingleton<IKafkaClientFactory, KafkaClientFactory>();

builder.Services.AddHostedService<KafkaTopicSeeder>();
// Feature flags to avoid duplicate tally producers.
var enableStream = string.Equals(Environment.GetEnvironmentVariable("ENABLE_STREAM_TOPOLOGY"), "true", StringComparison.OrdinalIgnoreCase);
var enableWorker = !enableStream || string.Equals(Environment.GetEnvironmentVariable("ENABLE_TALLY_WORKER"), "true", StringComparison.OrdinalIgnoreCase);
var enableMini = string.Equals(Environment.GetEnvironmentVariable("ENABLE_MINI_STREAM"), "true", StringComparison.OrdinalIgnoreCase);

if (enableStream)
{
    builder.Services.AddHostedService<StreamTallyHostedService>();
}

if (enableWorker)
{
    builder.Services.AddHostedService<TallyWorker>(); // Default path when stream topology disabled
}

if (enableMini)
{
    builder.Services.AddHostedService<MiniStreamHostedService>(); // Diagnostic only
}

var host = builder.Build();
await host.RunAsync();
