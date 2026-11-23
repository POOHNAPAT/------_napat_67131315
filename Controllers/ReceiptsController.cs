using System.Security.Claims;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize(Roles="Member")]
public class ReceiptsController(BookingStore store):Controller
{
    private long UserId=>long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public IActionResult Index(){Response.Headers["Cache-Control"]="no-store";return View(store.Receipts(UserId));}
    public IActionResult Details(string id)
    {
        Response.Headers["Cache-Control"]="no-store";
        var receipt=store.FindReceipt(id,UserId);
        return receipt is null?NotFound():View("Receipt",receipt);
    }
}
