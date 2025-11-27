namespace TallyService.Streaming;

using System;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Streamiz.Kafka.Net.SerDes;

public sealed class ConfluentJsonSerDes<T> : ISerDes<T>
    where T : class
{
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly JsonSerializerConfig _serializerConfig;
    private JsonSerializer<T>? _serializer;
    private JsonDeserializer<T>? _deserializer;

    public ConfluentJsonSerDes(
        ISchemaRegistryClient schemaRegistryClient,
        JsonSerializerConfig? serializerConfig = null)
    {
        _schemaRegistryClient = schemaRegistryClient ?? throw new ArgumentNullException(nameof(schemaRegistryClient));
        _serializerConfig = serializerConfig ?? new JsonSerializerConfig();
    }

    public void Initialize(SerDesContext context)
    {
        _serializer ??= new JsonSerializer<T>(_schemaRegistryClient, _serializerConfig);
        _deserializer ??= new JsonDeserializer<T>(_schemaRegistryClient);
    }

    public T Deserialize(byte[] data, SerializationContext context)
    {
        if (data is null)
        {
            return default!;
        }

        EnsureInitialized();
        return _deserializer!.DeserializeAsync(data, false, context)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    public byte[] Serialize(T data, SerializationContext context)
    {
        EnsureInitialized();
        return _serializer!.SerializeAsync(data, context)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    object ISerDes.DeserializeObject(byte[] data, SerializationContext context)
        => Deserialize(data, context)!;

    byte[] ISerDes.SerializeObject(object data, SerializationContext context)
    {
        if (data is null)
        {
            return Array.Empty<byte>();
        }

        if (data is not T typed)
        {
            throw new InvalidOperationException($"Unable to serialize type {data.GetType().FullName}; expected {typeof(T).FullName}.");
        }

        return Serialize(typed, context);
    }

    private void EnsureInitialized()
    {
        if (_serializer is not null && _deserializer is not null)
        {
            return;
        }

        _serializer ??= new JsonSerializer<T>(_schemaRegistryClient, _serializerConfig);
        _deserializer ??= new JsonDeserializer<T>(_schemaRegistryClient);
    }
}
