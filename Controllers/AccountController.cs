using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public class AccountController(BookingStore store) : Controller
{
    private bool AdminPortal => HttpContext.Items["AdminPortal"] is true;
    [HttpGet] public IActionResult Register(string? returnUrl) => View(new RegisterForm { ReturnUrl = returnUrl });
    [HttpPost]
    public IActionResult Register(RegisterForm form)
    {
        if (!ModelState.IsValid) return View(form);
        if (!store.Register(form))
        {
            ModelState.AddModelError(nameof(form.Email), "อีเมลนี้มีบัญชีอยู่แล้ว กรุณาเข้าสู่ระบบ");
            return View(form);
        }
        TempData["Success"] = "สมัครสมาชิกสำเร็จ กรุณาเข้าสู่ระบบเพื่อเริ่มจองสนาม";
        return RedirectToAction(nameof(Login), new { returnUrl = Url.IsLocalUrl(form.ReturnUrl) ? form.ReturnUrl : null });
    }
    [HttpGet] public IActionResult Login(string? returnUrl) => View(new LoginForm { ReturnUrl = returnUrl });
    [HttpPost]
    public async Task<IActionResult> Login(LoginForm form)
    {
        if (!ModelState.IsValid) return View(form);
        var user = store.Authenticate(form.Email, form.Password);
        if (user is null) { ModelState.AddModelError("", "อีเมลหรือรหัสผ่านไม่ถูกต้อง"); return View(form); }
        if (AdminPortal != (user.Role == "Admin"))
        {
            ModelState.AddModelError("", AdminPortal ? "หน้านี้สำหรับบัญชีผู้ดูแลระบบเท่านั้น" : "กรุณาเข้าสู่ระบบผู้ดูแลที่พอร์ตผู้ดูแลระบบ");
            return View(form);
        }
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Role, user.Role)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(new ClaimsPrincipal(identity));
        if (Url.IsLocalUrl(form.ReturnUrl)) return LocalRedirect(form.ReturnUrl!);
        return user.Role == "Admin" ? RedirectToAction("Index", "Admin") : RedirectToAction("Index", "Courts");
    }
    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return AdminPortal ? RedirectToAction(nameof(Login)) : RedirectToAction("Index", "Home");
    }
    [Authorize] public IActionResult Profile() => View(store.FindUser(long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)));
    public IActionResult Denied() { Response.StatusCode = 403; return View(); }
}
