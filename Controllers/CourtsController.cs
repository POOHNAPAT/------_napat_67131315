using CourtBook.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
    public IActionResult Index(DateOnly? date, long? courtId, long? sportTypeId, string? sport)
    {
        var selected = date ?? CourtTime.Today.AddDays(1);
        var courts = store.Courts().Where(c => !courtId.HasValue || c.Id == courtId).ToList();
        courts=courts.Where(c=>(!sportTypeId.HasValue || c.SportTypeId==sportTypeId)
            && (string.IsNullOrWhiteSpace(sport)||SportsAssets.Key(c.SportTypeName)==sport)).ToList();
        ViewBag.AllCourts = store.Courts();
        ViewBag.SportTypes=store.SportTypes();ViewBag.SportTypeId=sportTypeId;
        return View(new SchedulePage(selected, courtId, courts.Select(c => new CourtSchedule(c, store.Slots(c, selected))).ToList()));
    }
    public IActionResult Details(long id)
    {
        var court=store.FindCourt(id);if(court is null)return NotFound();
        var userId=long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var parsed)?parsed:0;
        return View(new CourtDetailsPage(court,store.Reviews(id),userId>0&&store.CanReview(userId,id)));
    }
    [Authorize(Roles="Member"),HttpPost]
    public IActionResult Review(ReviewForm form)
    {
        if(!ModelState.IsValid){TempData["Error"]="กรุณาให้คะแนน 1–5 และข้อความไม่เกิน 500 ตัวอักษร";return RedirectToAction(nameof(Details),new{id=form.CourtId});}
        try{store.Review(long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),form);TempData["Success"]="บันทึกรีวิวแล้ว";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return RedirectToAction(nameof(Details),new{id=form.CourtId});
    }

}
