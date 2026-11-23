using CourtBook.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public class HomeController(BookingStore store) : Controller
{
    public IActionResult Index()
    {
        ViewBag.Plans=store.Plans();ViewBag.Promotions=store.Promotions();return View(store.Courts());
    }
    public IActionResult About()=>View();
    public IActionResult Membership()
    {
        ViewData["AllowPlanPurchase"]=true;
        if(User.IsInRole("Member")&&long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id))
            ViewBag.MembershipWallet=store.Wallet(id);
        return View(store.Plans());
    }
    public IActionResult Promotions() => View(store.Promotions());
    public IActionResult Contact() => View();
    public IActionResult Error() { Response.StatusCode = 500; return View(); }
}
