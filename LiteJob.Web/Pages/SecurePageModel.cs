using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiteJob.Web.Pages;

[AllowAnonymous]
public abstract class SecurePageModel : PageModel
{
    public string Username => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.Identity?.Name ?? "";

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            context.Result = new RedirectResult("/login");
        }
    }
}
