using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public class CourtsController(BookingStore store) : Controller
{
    [HttpGet]
    public IActionResult Image(long id)
    {
        var image = store.FindCourtImage(id);
        if (image is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(image.Data, image.ContentType);
    }
    public IActionResult Index(DateOnly? date, long? courtId)
    {
        var selected = date ?? CourtTime.Today.AddDays(1);
        var courts = store.Courts().Where(c => !courtId.HasValue || c.Id == courtId).ToList();
        ViewBag.AllCourts = store.Courts();
        return View(new SchedulePage(selected, courtId, courts.Select(c => new CourtSchedule(c, store.Slots(c, selected))).ToList()));
    }
}
