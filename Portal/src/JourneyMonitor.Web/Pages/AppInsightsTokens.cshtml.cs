using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JourneyMonitor.Web.Pages;

/// <summary>
/// Retired: token management lives in the portal's Settings → Access Tokens section, which talks
/// to the same /api/appinsights/tokens endpoints. Kept only so old bookmarks keep working.
/// </summary>
public class AppInsightsTokensModel : PageModel
{
    public IActionResult OnGet() => Redirect("/?settings=tokens");
}
