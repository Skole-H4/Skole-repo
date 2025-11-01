using System;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Streamiz.Kafka.Net.SerDes;
using TallyService.Models;

namespace TallyService.Services;

/// <summary>
/// A tolerant JSON SerDes for VoteEnvelope that suppresses noisy exceptions from malformed payloads.
/// Strategy:
///  - Fast pre-check: if first non-whitespace char != '{' -> treat as malformed (returns envelope with empty Option)
///  - Attempt full JSON deserialize with Newtonsoft.Json; on failure classify error kind and return empty envelope.
///  - Maintains counters; logs first few malformed payload previews then summarizes periodically.
/// This prevents the upstream SourceProcessor from spamming error logs via exception propagation.
/// </summary>
public sealed class TolerantVoteEnvelopeSerDes : ISerDes<VoteEnvelope>
{
    private readonly ILogger<TolerantVoteEnvelopeSerDes> _logger;
    private readonly int _previewLimit;
    private readonly int _warnLimit;
    private readonly int _summaryInterval;
    private long _errorCount;
    private long _lastSummaryCount;

    public TolerantVoteEnvelopeSerDes(ILogger<TolerantVoteEnvelopeSerDes> logger)
    {
        _logger = logger;
        _previewLimit = ParseEnvInt("KAFKA_DESER_PREVIEW_LIMIT", 120, 16, 10_000);
        _warnLimit = ParseEnvInt("KAFKA_TOLERANT_WARN_LIMIT", 10, 0, 10_000);
        _summaryInterval = ParseEnvInt("KAFKA_TOLERANT_SUMMARY_INTERVAL", 100, 10, 100_000);
        _logger.LogInformation("TolerantVoteEnvelopeSerDes config warn.limit={Warn} summary.interval={Summary} preview.limit={Preview}", _warnLimit, _summaryInterval, _previewLimit);
    }

    // Underlying delegate for normal serialization when payloads are valid
    private readonly JsonSerDes<VoteEnvelope> _delegate = new();

    public void Initialize(SerDesContext context)
    {
        _delegate.Initialize(context);
    }

    public VoteEnvelope Deserialize(byte[] data, SerializationContext context)
    {
        if (data == null || data.Length == 0)
        {
            return EmptyEnvelope();
        }
        var firstChar = FirstNonWhitespaceChar(data);
        if (firstChar != '{')
        {
            LogMalformed(data, "non-json-prefix", $"firstChar='{firstChar}'");
            return EmptyEnvelope();
        }
        try
        {
            var json = Encoding.UTF8.GetString(data);
            // Attempt direct nested form via delegate first
            var nested = _delegate.Deserialize(data, context);
            if (nested != null && nested.Event != null && !string.IsNullOrWhiteSpace(nested.Event.Option))
            {
                return nested;
            }
            // Fallback: attempt flattened form parsing (Option, UserId, CityTopic, City, ZipCode at root)
            try
            {
                dynamic? flat = JsonConvert.DeserializeObject(json);
                if (flat != null)
                {
                    // If event missing but root has Option/UserId, synthesize VoteEnvelope
                    string? option = TryGetString(flat, "Option") ?? TryGetString(flat, "option");
                    string? userId = TryGetString(flat, "UserId") ?? TryGetString(flat, "userId");
                    if (!string.IsNullOrWhiteSpace(option) && !string.IsNullOrWhiteSpace(userId))
                    {
                        var envelope = new VoteEnvelope
                        {
                            Event = new VoteEvent { Option = option!, UserId = userId!, Timestamp = DateTimeOffset.UtcNow },
                            CityTopic = TryGetString(flat, "CityTopic") ?? TryGetString(flat, "cityTopic"),
                            City = TryGetString(flat, "City") ?? TryGetString(flat, "city"),
                            ZipCode = TryGetNullableInt(flat, "ZipCode") ?? TryGetNullableInt(flat, "zipCode")
                        };
                        return envelope;
                    }
                }
            }
            catch (Exception flatEx)
            {
                LogMalformed(data, "flat-parse", flatEx.Message);
            }
            return EmptyEnvelope();
        }
        catch (JsonReaderException ex)
        {
            LogMalformed(data, "json-reader", ex.Message);
            return EmptyEnvelope();
        }
        catch (Exception ex)
        {
            LogMalformed(data, "json-generic", ex.Message);
            return EmptyEnvelope();
        }
    }

    public byte[] Serialize(VoteEnvelope data, SerializationContext context)
    {
        return _delegate.Serialize(data, context);
    }

    // Non-generic interface adaptation methods
    public object DeserializeObject(byte[] data, SerializationContext context) => Deserialize(data, context);
    public byte[] SerializeObject(object data, SerializationContext context) => Serialize((VoteEnvelope)data, context);


    private void LogMalformed(byte[] data, string category, string details)
    {
        var count = System.Threading.Interlocked.Increment(ref _errorCount);
        if (count <= _warnLimit)
        {
            var preview = TruncateForPreview(data, _previewLimit);
            _logger.LogWarning("Malformed vote envelope #{Count} category={Category} details={Details} preview='{Preview}'", count, category, details, preview);
        }
        else if (_summaryInterval > 0 && count % _summaryInterval == 0)
        {
            var delta = count - _lastSummaryCount;
            _lastSummaryCount = count;
            _logger.LogWarning("Malformed vote envelopes accumulated total={Total} (+{Delta} since last summary) last.category={Category} last.details={Details}", count, delta, category, details);
        }
        else
        {
            _logger.LogDebug("Malformed vote envelope suppressed count={Count} category={Category} details={Details}", count, category, details);
        }
    }

    private static char FirstNonWhitespaceChar(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            var c = (char)data[i];
            if (!char.IsWhiteSpace(c)) return c;
        }
        return '\0';
    }

    private static VoteEnvelope EmptyEnvelope() => new() { Event = new VoteEvent { UserId = string.Empty, Option = string.Empty, Timestamp = DateTimeOffset.UtcNow } };

    private static string TruncateForPreview(byte[] data, int limit)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.Length <= limit) return text;
            return text.Substring(0, limit) + "…";
        }
        catch
        {
            return $"<binary:{data.Length} bytes>";
        }
    }

    private static int ParseEnvInt(string name, int @default, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return @default;
        if (!int.TryParse(raw, out var value)) return @default;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string? TryGetString(dynamic obj, string name)
    {
        try
        {
            var value = obj[name];
            if (value == null) return null;
            string s = value.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    private static int? TryGetNullableInt(dynamic obj, string name)
    {
        try
        {
            var value = obj[name];
            if (value == null) return null;
            if (int.TryParse(value.ToString(), out int i)) return i;
            return null;
        }
        catch { return null; }
    }
}
