using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LicensePlatform.Application.Security;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace LicensePlatform.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class LoginModel(IAdminAuthenticationService authenticationService) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Admin/Dashboard")
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var sourceIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await authenticationService.AuthenticateAsync(
            Input.UserName.Trim(),
            Input.Password,
            sourceIp,
            cancellationToken);

        if (!result.Succeeded
            || result.AdminId is null
            || string.IsNullOrWhiteSpace(result.UserName)
            || string.IsNullOrWhiteSpace(result.CredentialStamp))
        {
            ModelState.AddModelError(string.Empty, result.LockedUntil is not null
                ? $"登录失败次数过多，请在 {result.LockedUntil.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 后重试。"
                : "账号或密码错误。");
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, result.AdminId.Value.ToString()),
            new Claim(ClaimTypes.Name, result.UserName),
            new Claim(ClaimTypes.Role, "SuperAdmin"),
            new Claim(AdminCredentialStamp.ClaimType, result.CredentialStamp)
        };

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = true,
                IsPersistent = false,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
            });

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Admin/Dashboard");
    }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "请输入管理员账号。")]
        [Display(Name = "管理员账号")]
        [StringLength(100)]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "请输入密码。")]
        [Display(Name = "密码")]
        [DataType(DataType.Password)]
        [StringLength(512)]
        public string Password { get; set; } = string.Empty;
    }
}
