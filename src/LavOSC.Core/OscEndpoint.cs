namespace Laveyana.LavOSC.Core;

public enum OscEndpointDirection { Receive, Send }

public sealed record OscEndpoint(string Address, OscEndpointDirection Direction, string Arguments,
    string? Example = null, string? Notes = null);

/// <summary>Provides the OSC reference displayed in the module catalog.</summary>
public interface IOscEndpointDocumentation
{
    IReadOnlyList<OscEndpoint> Endpoints { get; }
}
