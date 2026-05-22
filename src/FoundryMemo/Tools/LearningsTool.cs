// Copyright (c) foundry-memo. All rights reserved.

using System.ComponentModel;
using System.Text;
using FoundryMemo.Models;
using FoundryMemo.Services;

namespace FoundryMemo.Tools;

/// <summary>
/// Agent tools for reading and writing process learnings.
/// These are global, non-sensitive, operational improvements only.
/// </summary>
public class LearningsTool
{
    private readonly LearningsStore _store;

    public LearningsTool(LearningsStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Reads all process learnings. Call this at the start of every memo generation
    /// to apply lessons learned from previous runs.
    /// </summary>
    [Description("Reads all process learnings from memory. Call this FIRST before starting any memo generation to apply past improvements. Returns operational insights about retrieval, summarization, and PDF generation.")]
    public async Task<string> ReadLearnings()
    {
        var learnings = await _store.ReadAllAsync();

        if (learnings.Count == 0)
        {
            return "No prior learnings found. This is the first run — proceed with defaults and record any insights after completion.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Process Learnings ({learnings.Count} entries)");
        sb.AppendLine();

        var grouped = learnings.GroupBy(l => l.Category);
        foreach (var group in grouped)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var entry in group)
            {
                sb.AppendLine($"- {entry.Learning}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes a new process learning after completing a memo generation.
    /// Only store operational insights — NEVER store content, URLs, or user data.
    /// </summary>
    [Description("Writes a new process learning after completing a memo. Only store operational insights about HOW to improve the process. NEVER store file content, user data, SharePoint URLs, or any sensitive information. Categories: 'retrieval', 'summarization', 'pdf_generation', 'error_handling', 'general'.")]
    public async Task<string> WriteLearning(
        [Description("The process insight to remember. Must be operational only, e.g. 'Large sites need multiple retrieval queries' or 'Tables render better as bullet lists in PDFs'")] string learning,
        [Description("Category: 'retrieval', 'summarization', 'pdf_generation', 'error_handling', or 'general'")] string category)
    {
        var entry = new LearningEntry
        {
            Learning = learning,
            Category = category
        };

        await _store.WriteAsync(entry);

        return $"Learning recorded [{category}]: {learning}";
    }
}
