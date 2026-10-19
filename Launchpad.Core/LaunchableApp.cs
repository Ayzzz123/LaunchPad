namespace Launchpad.Core;

public sealed record LaunchableApp(
    string Id,
    string Name,
    string ParsingName,
    string Arguments,
    string WorkingDirectory)
{
    public string? IconSource { get; init; }
}
