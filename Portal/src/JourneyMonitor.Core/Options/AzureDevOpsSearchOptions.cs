namespace JourneyMonitor.Core.Options;

/// <summary>
/// Azure DevOps search configuration (WI 239285, Fase 1). Backs the DocsAgent, and later the
/// CodeAgent, with the hosted indexes Azure DevOps already maintains.
///
/// Deliberately not a local index: a copy of someone else's repositories and wikis is a second
/// thing to build, keep fresh and secure, and it is stale the moment it is written. The one thing
/// a local index would add - working offline - is worth nothing to an agent whose other evidence
/// source is Application Insights.
///
/// When this is not configured the specialists stay sourceless and say so, which is the honest
/// state. They never degrade into "found nothing".
/// </summary>
public sealed class AzureDevOpsSearchOptions
{
    public const string SectionName = "AzureDevOpsSearch";

    /// <summary>Master switch. Default false: reading another team's repositories is opt-in, not something a fresh clone starts doing.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Organisation, e.g. "arrivia". No default - guessing an organisation name is not a sane fallback.</summary>
    public string? Organization { get; set; }

    /// <summary>Project, e.g. "SoftEng".</summary>
    public string? Project { get; set; }

    /// <summary>
    /// Tenant the token is issued for. Azure DevOps lives in a different tenant from the
    /// Application Insights resources this platform reads, and a token from the wrong one comes
    /// back as a redirect to a login page rather than as an error anyone would notice.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Pre-minted token, used ahead of the credential chain. The escape hatch for a host with no
    /// Managed Identity and no usable `az login` - which is every local Docker run, where the
    /// chain can only reach IMDS at 169.254.169.254 and fails.
    ///
    /// A secret: supply it as the AzureDevOpsSearch__AccessToken environment variable, never in a
    /// committed settings file. It must be minted for the Azure DevOps resource
    /// (499b84ac-1321-427f-aa17-267ca6975798/.default); a token for ARM or Application Insights is
    /// still a valid token, so Azure DevOps answers it with a sign-in page instead of an auth
    /// error. It expires in about an hour, so the durable option is the encrypted token store,
    /// which is read first and can be refreshed without restarting the container.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Repositories the code search may look in, comma-separated. EMPTY MEANS ALL, which is the
    /// configured default and a deliberate decision.
    ///
    /// The cost of "all" is that the organisation holds legacy, forked and proof-of-concept
    /// repositories, so a model can cite a real file in code that no longer runs - and grounding
    /// will confirm it, because the file genuinely exists. The mitigation is reporting, not
    /// filtering: every result carries its repository, and <see cref="ArchivedRepositoryMarkers"/>
    /// flags the ones whose name says they are not live.
    /// </summary>
    public string? Repositories { get; set; }

    /// <summary>
    /// Substrings in a repository name that mark a result as probably-not-live. Default
    /// "legacy,poc,forked,sandbox,test,template,archive". Reported, never silently dropped: a
    /// legacy repository is sometimes exactly where the bug is.
    /// </summary>
    public string? NotLiveRepositoryMarkers { get; set; }

    /// <summary>Results per search. Default 5 - this text is pasted into a prompt that is already large.</summary>
    public int? MaxResults { get; set; }

    /// <summary>Seconds before a search is abandoned. Default 20; a slow search must not hold an investigation open.</summary>
    public int? TimeoutSeconds { get; set; }

    public bool ResolvedEnabled => Enabled ?? false;

    /// <summary>Configured means all three of enabled, organisation and project - a partial configuration is not a usable source.</summary>
    public bool IsConfigured =>
        ResolvedEnabled
        && !string.IsNullOrWhiteSpace(Organization)
        && !string.IsNullOrWhiteSpace(Project);

    public IReadOnlyList<string> ResolvedRepositories =>
        string.IsNullOrWhiteSpace(Repositories)
            ? []
            : Repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> ArchivedRepositoryMarkers =>
        string.IsNullOrWhiteSpace(NotLiveRepositoryMarkers)
            ? ["legacy", "poc", "forked", "sandbox", "test", "template", "archive"]
            : NotLiveRepositoryMarkers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int ResolvedMaxResults => MaxResults is > 0 ? MaxResults.Value : 5;

    public int ResolvedTimeoutSeconds => TimeoutSeconds is > 0 ? TimeoutSeconds.Value : 20;
}
