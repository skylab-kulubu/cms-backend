using System.Text.Json.Nodes;
using Skylab.Cms.Domain.Enums;

namespace Skylab.Cms.Domain.Entities;

public sealed class ContentBlock : Entity
{
    public string Slug { get; private set; } = default!;
    public string BlockPath { get; private set; } = default!;
    public BlockType BlockType { get; private set; }
    public JsonNode Value { get; private set; } = default!;
    public int SortOrder { get; private set; }
    public string UpdatedBy { get; private set; } = default!;
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAt { get; private set; }

    private ContentBlock() { }

    public static ContentBlock Create(
        string slug,
        string blockPath,
        BlockType blockType,
        JsonNode value,
        int sortOrder,
        string updatedBy,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);
        ArgumentNullException.ThrowIfNull(value);

        return new ContentBlock
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            BlockPath = blockPath,
            BlockType = blockType,
            Value = value,
            SortOrder = sortOrder,
            UpdatedBy = updatedBy,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            Version = 1
        };
    }

    public void UpdateValue(JsonNode value, string updatedBy, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        Value = value;
        UpdatedBy = updatedBy;
        UpdatedAt = utcNow;
        Version += 1;
    }

    public void Reorder(int sortOrder, string updatedBy, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        SortOrder = sortOrder;
        UpdatedBy = updatedBy;
        UpdatedAt = utcNow;
        Version += 1;
    }

    public void Archive(string updatedBy, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        if (IsArchived)
        {
            return;
        }

        IsArchived = true;
        ArchivedAt = utcNow;
        UpdatedBy = updatedBy;
        UpdatedAt = utcNow;
        Version += 1;
    }

    public void Restore(string updatedBy, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedBy);

        if (!IsArchived)
        {
            return;
        }

        IsArchived = false;
        ArchivedAt = null;
        UpdatedBy = updatedBy;
        UpdatedAt = utcNow;
        Version += 1;
    }
}