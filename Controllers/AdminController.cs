using CourtBook.Models;
using System.Security.Claims;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController(BookingStore store) : Controller
{
    private long AdminId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public IActionResult Plans() => View(store.Plans());
    [HttpPost]
    public IActionResult Plans(PlanForm form)
    {
        if(ModelState.IsValid)
        {
            try { if(!store.SavePlan(form)) return NotFound(); TempData["Success"]="บันทึกแพ็กแล้ว สิทธิ์ที่ซื้อไปแล้วจะคงเงื่อนไขเดิม"; return RedirectToAction(nameof(Plans)); }
            catch(BookingRuleException ex) { ModelState.AddModelError("",ex.Message); }
        }
        return View(store.Plans());
    }
    [HttpGet] public IActionResult TopUps() => View(store.TopUps());
    [HttpPost]
    public IActionResult ReviewTopUp(long id, bool approve, string? note)
    {
        if(!ModelState.IsValid) return BadRequest();
        try
        {
            if(!store.ReviewTopUp(id,AdminId,approve,note)) { TempData["Error"]="ไม่พบรายการที่รอตรวจ หรือรายการนี้ตรวจไปแล้ว"; }
            else TempData["Success"]=approve?"อนุมัติและเพิ่มยอดเข้ากระเป๋าแล้ว":"ไม่อนุมัติคำขอเติมเงินแล้ว";
        }
        catch(BookingRuleException ex) { TempData["Error"]=ex.Message; }
        return RedirectToAction(nameof(TopUps));
    }
    [HttpGet]
    public IActionResult TopUpSlip(long id)
    {
        var image=store.TopUpSlip(id);
        if(image is null) return NotFound();
        Response.Headers["Cache-Control"]="no-store";
        Response.Headers["X-Content-Type-Options"]="nosniff";
        return File(image.Data,image.ContentType);
    }
    public IActionResult Customers() => View(store.Customers());
    public IActionResult Customer(long id)
    {
        var account = store.Customers().SingleOrDefault(c => c.Id == id);
        return account is null ? NotFound() : View(new CustomerPage(account, store.Bookings(id)));
    }
    [HttpGet] public IActionResult SportTypes() => View(store.SportTypes());
    [HttpPost]
    public IActionResult SportTypes(SportTypeForm form)
    {
        if (ModelState.IsValid)
        {
            try { store.AddSportType(form); TempData["Success"] = "เพิ่มประเภทกีฬาเรียบร้อยแล้ว"; return RedirectToAction(nameof(SportTypes)); }
            catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); }
        }
        return View(store.SportTypes());
    }
    [HttpPost]
    public IActionResult ConfirmBooking(long id)
    {
        if (!store.Bookings().Any(b => b.Id == id)) return NotFound();
        if (store.ConfirmBooking(id, AdminId)) TempData["Success"] = "ยืนยันการจองเรียบร้อยแล้ว";
        else TempData["Error"] = "รายการนี้ได้รับการยืนยันแล้ว";
        return RedirectToAction(nameof(Booking), new { id });
    }
    [HttpPost]
    public IActionResult ConfirmPayment(long id, PaymentConfirmationForm form)
    {
        var booking = store.Bookings().SingleOrDefault(b => b.Id == id);
        if (booking is null) return NotFound();
        if (!ModelState.IsValid) return View("Booking", booking);
        try
        {
            if (!store.ConfirmPayment(id, AdminId, form.Reference))
                ModelState.AddModelError("", "ต้องยืนยันการจองก่อน และรายการนี้ต้องยังไม่ได้ยืนยันชำระเงิน");
        }
        catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); }
        if (!ModelState.IsValid) return View("Booking", booking);
        TempData["Success"] = "ยืนยันการชำระเงินเรียบร้อยแล้ว";
        return RedirectToAction(nameof(Booking), new { id });
    }
    public IActionResult Index()
    {
        var bookings = store.Bookings();
        return View(new AdminOverview(store.Courts(true).Count, bookings.Count, store.Promotions(true).Count, bookings.Take(6).ToList()));
    }
    public IActionResult Courts() => View(store.Courts(true));
    [HttpGet]
    public IActionResult Court(long id = 0)
    {
        ViewBag.SportTypes = store.SportTypes();
        if (id == 0) return View(new CourtForm());
        var court = store.FindCourt(id);
        return court is null ? NotFound() : View(new CourtForm { Id=court.Id,Name=court.Name,Description=court.Description,Price=court.PriceSatang/100m,IsActive=court.IsActive,SportTypeId=court.SportTypeId,CurrentImageUrl=court.ImageUrl });
    }
    [HttpPost]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public IActionResult Court([FromRoute] long id, CourtForm form)
    {
        ViewBag.SportTypes = store.SportTypes();
        if (id != form.Id) return BadRequest();
        var court = id == 0 ? null : store.FindCourt(id);
        if (id != 0 && court is null) return NotFound();
        form.CurrentImageUrl = court?.ImageUrl;
        if (!ModelState.IsValid) return View(form);
        try { if (!store.SaveCourt(form)) return NotFound(); }
        catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); return View(form); }
        TempData["Success"] = "บันทึกข้อมูลสนามเรียบร้อยแล้ว";
        return RedirectToAction(nameof(Courts));
    }
    [HttpPost]
    public IActionResult DeleteCourt(long id)
    {
        if (!store.DeleteCourt(id)) return NotFound();
        TempData["Success"] = "ลบสนามเรียบร้อยแล้ว ประวัติและรายการจองเดิมยังคงอยู่";
        return RedirectToAction(nameof(Courts));
    }
    public IActionResult Schedule(long? courtId, DateOnly? date)
    {
        var court = store.FindCourt(courtId ?? store.Courts(true).FirstOrDefault()?.Id ?? 0);
        if (court is null) return RedirectToAction(nameof(Courts));
        var selected = date ?? CourtTime.Today.AddDays(1);
        return View(new AdminSchedulePage(court, selected, store.Courts(true), store.Slots(court,selected,true)));
    }
    [HttpPost]
    public IActionResult Schedule(long courtId, DateOnly date, int[] blocked)
    {
        if (!ModelState.IsValid) TempData["Error"] = "วันที่หรือสนามไม่ถูกต้อง";
        else
        {
            try { store.SaveSchedule(courtId,date,blocked); TempData["Success"] = "บันทึกช่วงเวลาเปิด–ปิดเรียบร้อยแล้ว"; }
            catch (BookingRuleException ex) { TempData["Error"] = ex.Message; }
        }
        return RedirectToAction(nameof(Schedule),new { courtId, date = CourtTime.Iso(date == default ? CourtTime.Today : date) });
    }
    public IActionResult Bookings(DateOnly? date, long? courtId, string? status) => View(new BookingListPage(
        store.Bookings(null,date,courtId).Where(b=>string.IsNullOrEmpty(status)||b.Status==status).ToList(),date,status,courtId,store.Courts(true)));
    public IActionResult Booking(long id)
    {
        var booking = store.Bookings().SingleOrDefault(b=>b.Id==id);
        return booking is null ? NotFound() : View(booking);
    }
    [HttpGet]
    public IActionResult PaymentSlip(long id)
    {
        var slip = store.FindPaymentSlip(id);
        if (slip is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(slip.Data, slip.ContentType);
    }
    public IActionResult Promotions() => View(store.Promotions(true));
    [HttpGet]
    public IActionResult Promotion(long id = 0)
    {
        if(id==0) return View(new PromotionForm());
        var promo=store.Promotions(true).SingleOrDefault(p=>p.Id==id);
        return promo is null ? NotFound() : View(new PromotionForm {
            Id=promo.Id,Name=promo.Name,Rule=promo.Rule,Kind=promo.Kind,Value=promo.Value100/100m,MinHours=promo.MinHours,
            MinDays=promo.MinDays,MinSpend=promo.MinSpendSatang/100m,Starts=promo.Starts,Ends=promo.Ends,IsActive=promo.IsActive });
    }
    [HttpPost]
    public IActionResult Promotion([FromRoute] long id, PromotionForm form)
    {
        if (id!=form.Id) return BadRequest();
        if (!ModelState.IsValid) return View(form);
        try { if(!store.SavePromotion(form)) return NotFound(); }
        catch(BookingRuleException ex) { ModelState.AddModelError("",ex.Message); return View(form); }
        TempData["Success"]="บันทึกโปรโมชั่นเรียบร้อยแล้ว";
        return RedirectToAction(nameof(Promotions));
    }
}
