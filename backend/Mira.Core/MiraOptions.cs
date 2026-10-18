namespace Mira.Core;

public sealed class MiraOptions
{
    public const string SectionName = "Mira";

    public string ProviderMode { get; set; } = "Mock";

    public void Validate()
    {
        if (!string.Equals(ProviderMode, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("M0 supports only the Mock provider mode.");
        }
    }
}
