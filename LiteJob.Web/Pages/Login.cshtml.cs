using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiteJob.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    public void OnGet()
    {
    }
}
