using CourtBook.Models;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public partial class AdminController
{
    [HttpPost]
    public IActionResult ConfirmCheckOut(long id,CheckOutConfirmationForm form)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(ModelState.IsValid)
        {
            try{store.ConfirmBookingCheckOut(id,AdminId,form.EquipmentVerified);TempData["Success"]="เช็กเอาต์แล้ว ตรวจรับอุปกรณ์ครบและบันทึกผู้ตรวจเรียบร้อย";return RedirectToAction(nameof(Booking),new{id});}
            catch(BookingRuleException ex){ModelState.AddModelError("",ex.Message);}
        }
        var booking=store.Bookings().SingleOrDefault(b=>b.Id==id);return booking is null?NotFound():View("Booking",booking);
    }
    private CheckInPage CheckInPage(Booking? booking=null,string code="",string? method=null)=>new(booking,code,method,store.Bookings(date:CourtTime.Today));
    [HttpGet]
    public IActionResult CheckIn(long? id)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!ModelState.IsValid)return BadRequest();
        if(!id.HasValue)return View(CheckInPage());
        var booking=store.Bookings().SingleOrDefault(b=>b.Id==id);return booking is null?NotFound():View(CheckInPage(booking,booking.Reference,"Code"));
    }
    [HttpPost]
    public IActionResult CheckInLookup(CheckInLookupForm form)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(ModelState.IsValid)
        {
            try{var entry=store.LookupEntry(form.Code);return View("CheckIn",CheckInPage(entry.Booking,form.Code.Trim(),entry.Method));}
            catch(BookingRuleException ex){ModelState.AddModelError("",ex.Message);}
        }
        return View("CheckIn",CheckInPage());
    }
    [HttpPost]
    public IActionResult ConfirmCheckIn(CheckInConfirmationForm form)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(ModelState.IsValid)
        {
            try
            {
                var id=store.ConfirmBookingCheckIn(AdminId,form.Code,form.IdentityVerified);
                TempData["Success"]="ยืนยันเช็กอินแล้ว บันทึกผู้ตรวจและเวลาเรียบร้อย";
                return RedirectToAction(nameof(CheckIn),new{id});
            }
            catch(BookingRuleException ex){ModelState.AddModelError("",ex.Message);}
        }
        try{var entry=store.LookupEntry(form.Code);return View("CheckIn",CheckInPage(entry.Booking,form.Code,entry.Method));}
        catch(BookingRuleException){return View("CheckIn",CheckInPage());}
    }
}
