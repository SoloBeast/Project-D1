using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Configuration;

public sealed class SystemConfiguration : AuditableEntity
{
    private SystemConfiguration() { }

    public SystemConfiguration(string key, string value, string valueType, string? description = null)
    {
        Key = key;
        Value = value;
        ValueType = valueType;
        Description = description;
    }

    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public string ValueType { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsSensitive { get; private set; }

    public void UpdateValue(string value, DateTime utcNow)
    {
        Value = value;
        SetUpdated(utcNow);
    }
}
