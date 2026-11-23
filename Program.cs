using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.ModelBinderProviders.Insert(0, new IsoDateModelBinder());
});
builder.Services.AddSingleton<BookingStore>();
builder.Services.AddSingleton<AccountRecovery>();
builder.Services.AddRateLimiter(options=>{
    options.RejectionStatusCode=429;
    options.AddPolicy("account-recovery",context=>RateLimitPartition.GetFixedWindowLimiter(
        (context.Connection.RemoteIpAddress?.ToString()??"unknown")+context.Request.Path.ToString(),
        _=>new FixedWindowRateLimiterOptions{PermitLimit=5,Window=TimeSpan.FromMinutes(15),QueueLimit=0}));
});
var adminPort = builder.Configuration.GetValue<int>("Portals:AdminPort");
var authentication = builder.Services.AddAuthentication("Portal")
    .AddPolicyScheme("Portal", null, options => options.ForwardDefaultSelector = context =>
        context.Connection.LocalPort == adminPort ? "AdminPortal" : "CustomerPortal");
foreach (var scheme in new[] { "CustomerPortal", "AdminPortal" })
authentication.AddCookie(scheme, options =>
{
    options.Cookie.Name = $"CourtBook.{scheme}";
    if (scheme == "CustomerPortal") options.CookieManager = new PortCookieManager();
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Denied";
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = long.TryParse(id, out var userId)
            ? context.HttpContext.RequestServices.GetRequiredService<BookingStore>().FindUser(userId) : null;
        if (user is null || context.Principal?.FindFirstValue(ClaimTypes.Role) != user.Role
            || context.Principal?.FindFirstValue("PasswordVersion") != AccountRecovery.Version(user)
            || (context.Scheme.Name == "AdminPortal") != (user.Role == "Admin"))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
var keyDirectory=builder.Configuration["DataProtection:KeyDirectory"];
if(!string.IsNullOrWhiteSpace(keyDirectory)&&!builder.Environment.IsEnvironment("Testing"))
{
    Directory.CreateDirectory(keyDirectory);
    builder.Services.AddDataProtection().SetApplicationName("COURTBOOK").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
}
// Only the isolated automated test process uses in-memory cookie keys.
if (builder.Environment.IsEnvironment("Testing"))
{
    if (!Path.IsPathFullyQualified(builder.Configuration["Database:Path"] ?? ""))
        throw new InvalidOperationException("Testing requires an absolute temporary database path.");
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
}
var app = builder.Build();
app.Services.GetRequiredService<BookingStore>();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    app.UseExceptionHandler("/Home/Error");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var admin = context.Connection.LocalPort == adminPort;
    context.Items["AdminPortal"] = admin;
    // Use the action MVC actually selected, so URL letter casing cannot change portal restrictions.
    var selectedAction = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
    var controller = selectedAction?.ControllerName;
    var action = selectedAction?.ActionName;
    if (admin && context.Request.Path == "/")
    {
        context.Response.Redirect("/Admin");
        return;
    }
    var accountPage = controller == "Account" && action is "Login" or "Logout" or "Denied" or "Profile" or "EditProfile" or "ForgotPassword" or "ResetPassword";
    if ((!admin && controller == "Admin") || (admin && controller != "Admin" && !accountPage
        && !(controller == "Home" && action == "Error") && !(controller == "Courts" && action == "Image") && context.Request.Path!="/healthz"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz",()=>Results.Text("ok")).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
