using System.Security.Claims;
using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<AppUser> _hasher = new();
    public AccountController(AppDbContext db) => _db = db;

    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel m)
    {
        if (!ModelState.IsValid) return View(m);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == m.Username);
        if (user == null || _hasher.VerifyHashedPassword(user, user.PasswordHash, m.Password) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError("", "Invalid username or password.");
            return View(m);
        }
        await SignIn(user);
        return !string.IsNullOrEmpty(m.ReturnUrl) && Url.IsLocalUrl(m.ReturnUrl)
            ? Redirect(m.ReturnUrl)
            : user.Role == Roles.Agronomist ? RedirectToAction("Index", "Agronomist")
            : RedirectToAction("Index", "Dashboard");
    }

    // The Self-registration is really limited to Farmer. Agronomist/Admin roles are assigned by an Admin (Admin/Users).
    [HttpGet, AllowAnonymous]
    public IActionResult Register() => View(new RegisterViewModel());

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel m)
    {
        if (!ModelState.IsValid) return View(m);
        if (await _db.Users.AnyAsync(u => u.Username == m.Username))
        {
            ModelState.AddModelError(nameof(m.Username), "That username is taken.");
            return View(m);
        }
        var user = new AppUser { Username = m.Username, Role = Roles.Farmer };
        user.PasswordHash = _hasher.HashPassword(user, m.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        await SignIn(user);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    public IActionResult Denied() => View();

    private Task SignIn(AppUser u)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Username),
            new Claim(ClaimTypes.Role, u.Role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }
}
