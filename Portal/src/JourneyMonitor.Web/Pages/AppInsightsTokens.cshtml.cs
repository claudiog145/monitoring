using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JourneyMonitor.Web.Pages;

/// <summary>
/// Page model for AppInsights Token Management UI.
/// Handles requests for /AppInsightsTokens Razor page.
/// The actual token operations are performed via API endpoints (POST /api/appinsights/tokens).
/// This page model just displays the UI and provides a controller-like context.
/// </summary>
public class AppInsightsTokensModel : PageModel
{
    private readonly ILogger<AppInsightsTokensModel> _logger;

    public AppInsightsTokensModel(ILogger<AppInsightsTokensModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogInformation("AppInsightsTokens page loaded");
    }
}
