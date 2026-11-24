using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize]
public class BookingsController(BookingStore store) : Controller
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private BookingPage Page(BookingForm form) => new(form, store.Courts(), store.FindCourt(form.CourtId) is Court court
        ? store.Slots(court, form.Date) : [], store.Promotions(),
        store.FindCourt(form.CourtId) is Court selected ? store.Equipment(selected.SportTypeId) : []);

    [HttpGet]
    public IActionResult Create(long? courtId, DateOnly? date, int? hour, long? promotionId)
    {
        var form = new BookingForm { CourtId = courtId ?? store.Courts().FirstOrDefault()?.Id ?? 0,
            Date = date ?? CourtTime.Today.AddDays(1), Hours = hour.HasValue ? [hour.Value] : [], PromotionId=promotionId };
        return View(Page(form));
    }
    [HttpGet]
    public IActionResult Quote(BookingForm form)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "วันที่หรือข้อมูลสนามไม่ถูกต้อง" });
        try { return Json(store.Preview(UserId, form)); }
        catch (BookingRuleException ex) { return BadRequest(new { error = ex.Message }); }
    }
    [HttpGet]
    public IActionResult EquipmentAvailability(long courtId,DateOnly date,int[] hours)
    {
        if(!ModelState.IsValid)return BadRequest(new {error="วันหรือเวลาที่เลือกไม่ถูกต้อง"});
        try{return Json(store.EquipmentAvailability(courtId,date,hours));}
        catch(BookingRuleException ex){return BadRequest(new {error=ex.Message});}
    }
    [HttpGet,Authorize(Roles="Member")]
    public IActionResult EquipmentHandover(long id)
    {
        Response.Headers["Cache-Control"]="no-store";
        var page=store.EquipmentHandover(id,UserId);return page is null?NotFound():View("EquipmentHandover",page);
    }
    [HttpGet,Authorize(Roles="Member")]
    public IActionResult EquipmentMovementImage(long id)
    {
        var image=store.EquipmentMovementImage(id,UserId);if(image is null)return NotFound();
        Response.Headers["Cache-Control"]="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";return File(image.Data,image.ContentType);
    }
    [HttpPost]
    public IActionResult Create(BookingForm form)
    {
        form.Hours ??= [];
        if (!ModelState.IsValid) return View(Page(form));
        try { return RedirectToAction(nameof(Result), new { id = store.Book(UserId, form) }); }
        catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); return View(Page(form)); }
    }
    [HttpPost, Authorize(Roles="Member")]
    public IActionResult PayWallet(long id)
    {
        if (!ModelState.IsValid) return BadRequest();
        try
        {
            if (!store.PayBookingWithWallet(id,UserId)) return NotFound();
            TempData["Success"]="ชำระค่าจองจากกระเป๋าเงินสำเร็จและออกใบเสร็จแล้ว กรุณาตรวจสถานะการยืนยันการจองในรายละเอียด";
        }
        catch(BookingRuleException ex) { TempData["Error"]=ex.Message; }
        return RedirectToAction(nameof(Result),new { id });
    }
    public IActionResult Result(long id)
    {
        var booking = store.Bookings(UserId).SingleOrDefault(b => b.Id == id);
        ViewBag.WalletBalance = store.WalletBalance(UserId);
        return booking is null ? NotFound() : View(booking);
    }
    [HttpGet,Authorize(Roles="Member")]
    public IActionResult EntryPass(long id)
    {
        Response.Headers["Cache-Control"]="no-store";Response.Headers["Referrer-Policy"]="no-referrer";
        var pass=store.EntryPass(id,UserId);return pass is null?NotFound():View(pass);
    }
    [HttpGet,Authorize(Roles="Member")]
    public IActionResult EntryQr(long id)
    {
        var pass=store.EntryPass(id,UserId);if(pass?.Payload is null)return NotFound();
        using var data=QRCoder.QRCodeGenerator.GenerateQrCode(pass.Payload,QRCoder.QRCodeGenerator.ECCLevel.M);
        using var image=new QRCoder.PngByteQRCode(data);
        Response.Headers["Cache-Control"]="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";
        return File(image.GetGraphic(8),"image/png");
    }
    [HttpGet]
    public IActionResult PaymentSlip(long id)
    {
        var slip = store.FindPaymentSlip(id, UserId);
        if (slip is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(slip.Data, slip.ContentType);
    }
    [HttpPost]
    public IActionResult Cancel(long id)
    {
        try
        {
            if (!store.CancelBooking(id, UserId)) return NotFound();
            TempData["Success"] = "ยกเลิกการจองแล้ว ช่วงเวลากลับมาเปิดให้จองตามตารางสนาม";
        }
        catch (BookingRuleException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Result), new { id });
    }
    [HttpGet,Authorize(Roles="Member")]
    public IActionResult ReportDamage(long? id)
    {
        var booking=id.HasValue?store.Bookings(UserId).SingleOrDefault(b=>b.Id==id):null;
        if(id.HasValue&&booking is null)return NotFound();
        return View(new DamageReportPage(booking,new DamageReportForm{Category=id.HasValue?"Court":"Other"},store.DamageReports(UserId,id)));
    }
    [HttpPost,Authorize(Roles="Member"),RequestSizeLimit(6*1024*1024)]
    public IActionResult ReportDamage(long? id,DamageReportForm form)
    {
        var booking=id.HasValue?store.Bookings(UserId).SingleOrDefault(b=>b.Id==id):null;
        if(id.HasValue&&booking is null)return NotFound();
        if(ModelState.IsValid)
        {
            try{store.ReportDamage(UserId,id,form);TempData["Success"]="ส่งปัญหาให้ผู้ดูแลแล้ว คุณติดตามคำตอบและสถานะได้ที่หน้านี้";return RedirectToAction(nameof(ReportDamage),new{id});}
            catch(BookingRuleException ex){ModelState.AddModelError("",ex.Message);}
        }
        return View(new DamageReportPage(booking,form,store.DamageReports(UserId,id)));
    }
    public IActionResult History(DateOnly? date, string? status)
    {
        var bookings = store.Bookings(UserId, date).Where(b => string.IsNullOrEmpty(status) || b.Status == status).ToList();
        return View(new BookingListPage(bookings,date,status,null,[]));
    }
}
