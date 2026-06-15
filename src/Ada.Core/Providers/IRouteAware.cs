namespace Ada.Core;

/// <summary>Implemented by a chat client that can report which route the last turn took, so the
/// engine can tag chunks and the UI can show an honest route badge.</summary>
public interface IRouteAware
{
    string CurrentRoute { get; }
}
