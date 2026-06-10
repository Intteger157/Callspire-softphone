using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Callspire.UpdateServer.Pages;

public class LoginModel : PageModel
{
    private readonly IConfiguration _configuration;

    public LoginModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Dashboard");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string user, string password, CancellationToken cancellationToken)
    {
        var expectedUser = _configuration["Admin:User"] ?? "admin";
        var expectedPass = _configuration["Admin:Password"];

        if (string.IsNullOrEmpty(expectedPass) || expectedPass == "CHANGE_ME_STRONG_PASSWORD")
        {
            Error = "Set Admin:Password in appsettings.json or environment variable Admin__Password before use.";
            return Page();
        }

        if (user == expectedUser && password == expectedPass)
        {
            var claims = new[] { new Claim(ClaimTypes.Name, user) };
            var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(id)).ConfigureAwait(false);

            return RedirectToPage("/Dashboard");
        }

        Error = "Invalid user or password.";
        return Page();
    }
}
