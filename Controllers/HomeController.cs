using CourtBook.Services;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public class HomeController(BookingStore store) : Controller
{
    public IActionResult Index() => View(store.Courts());
    public IActionResult Promotions() => View(store.Promotions());
    public IActionResult Contact() => View();
    public IActionResult Error() { Response.StatusCode = 500; return View(); }
}
