namespace JourneyMonitor.Core.Incidents;

/// <summary>
/// The incident tables are not available (connection string missing is reported separately).
/// Callers turn this into an empty Exceptions tab with a warning.
/// </summary>
public sealed class IncidentStoreUnavailableException(string message) : Exception(message);
