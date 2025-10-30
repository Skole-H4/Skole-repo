using System.Collections.Generic;

namespace WebApp.Models;

public sealed class VoteRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Option { get; set; } = string.Empty;
    public List<string> TargetTopics { get; set; } = new();
}
