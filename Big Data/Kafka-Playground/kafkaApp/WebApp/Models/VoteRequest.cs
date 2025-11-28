using System.Collections.Generic;

namespace WebApp.Models;

public sealed class VoteRequest
{
    // UserId removed; server now assigns a Guid per vote.
    public string Option { get; set; } = string.Empty;
    public List<string> TargetTopics { get; set; } = new();
}
