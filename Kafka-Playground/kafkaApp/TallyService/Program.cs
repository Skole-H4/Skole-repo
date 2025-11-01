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
builder.Services.AddOptions<KafkaFeatureFlags>()
    .Bind(builder.Configuration.GetSection("Kafka:Features"))
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
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<KafkaFeatureFlags>>().Value);

builder.Services.AddHostedService<KafkaTopicSeeder>();

var flagsProvider = builder.Services.BuildServiceProvider();
var flags = flagsProvider.GetService<KafkaFeatureFlags>() ?? new KafkaFeatureFlags();
if (flags.StreamTopologyEnabled)
{
    builder.Services.AddHostedService<StreamTallyHostedService>();
}
else
{
    builder.Services.AddHostedService<TallyWorker>();
}

var host = builder.Build();
await host.RunAsync();
