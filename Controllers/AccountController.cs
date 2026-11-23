using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public class AccountController(BookingStore store, AccountRecovery recovery) : Controller
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
            new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Role, user.Role), new Claim("PasswordVersion",AccountRecovery.Version(user))
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
    [Authorize, HttpGet]
    public IActionResult EditProfile()
    {
        var user=store.FindUser(long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
        return user is null?NotFound():View(new ProfileForm{Name=user.Name,Email=user.Email});
    }
    [Authorize, HttpPost]
    public async Task<IActionResult> EditProfile(ProfileForm form)
    {
        if(!ModelState.IsValid)return View(form);
        try
        {
            if(!store.UpdateProfile(long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),form))return NotFound();
            await HttpContext.SignOutAsync();
            TempData["Success"]="บันทึกข้อมูลแล้ว กรุณาเข้าสู่ระบบอีกครั้ง";
            return RedirectToAction(nameof(Login));
        }
        catch(BookingRuleException ex){ModelState.AddModelError("",ex.Message);return View(form);}
    }
    [HttpGet]
    public IActionResult ForgotPassword(){ViewBag.EmailConfigured=recovery.Configured;return View(new ForgotPasswordForm());}
    [HttpPost, Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("account-recovery")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordForm form)
    {
        ViewBag.EmailConfigured=recovery.Configured;
        if(!ModelState.IsValid)return View(form);
        if(!recovery.Configured){ModelState.AddModelError("","ระบบส่งอีเมลยังไม่ได้ตั้งค่า กรุณาติดต่อผู้ดูแล");return View(form);}
        try {await recovery.Send(form.Email,AdminPortal);}
        catch(Exception ex) when(ex is System.Net.Mail.SmtpException or InvalidOperationException){ModelState.AddModelError("","ส่งอีเมลไม่ได้ในขณะนี้ กรุณาติดต่อผู้ดูแล");return View(form);}
        ViewBag.Sent=true;return View(new ForgotPasswordForm());
    }
    [HttpGet]
    public IActionResult ResetPassword(string token)
    {
        ViewBag.ValidToken=recovery.Validate(token,AdminPortal) is not null;
        return View(new ResetPasswordForm{Token=token??""});
    }
    [HttpPost, Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("account-recovery")]
    public IActionResult ResetPassword(ResetPasswordForm form)
    {
        var user=recovery.Validate(form.Token,AdminPortal);ViewBag.ValidToken=user is not null;
        if(user is null)ModelState.AddModelError("","ลิงก์ไม่ถูกต้อง หมดอายุ หรือถูกใช้งานแล้ว");
        if(!ModelState.IsValid)return View(form);
        if(!store.ResetPassword(user!,form.Password)){ModelState.AddModelError("","ลิงก์ถูกใช้งานแล้ว กรุณาขอใหม่");return View(form);}
        TempData["Success"]="เปลี่ยนรหัสผ่านแล้ว กรุณาเข้าสู่ระบบด้วยรหัสผ่านใหม่";return RedirectToAction(nameof(Login));
    }
    public IActionResult Denied() { Response.StatusCode = 403; return View(); }
}
