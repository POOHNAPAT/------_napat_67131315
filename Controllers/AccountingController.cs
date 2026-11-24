using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize(Roles="Member")]
public class AccountingController(BookingStore store):Controller
{
    private long UserId=>long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet]
    public IActionResult Index(DateOnly? from,DateOnly? to)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!ModelState.IsValid)return BadRequest();
        try
        {
            var account=store.Accounting(from??new DateOnly(CourtTime.Today.Year,CourtTime.Today.Month,1),to??CourtTime.Today,UserId);
            var refunds=store.Refunds(UserId);
            var bookings=store.Bookings(UserId).Where(b=>b.CheckIn is null&&b.PaidAt is not null&&b.NetSatang>0&&(b.ApprovalStatus is "Pending" or "Confirmed")
                && b.Date.ToDateTime(new TimeOnly(b.StartHour,0))>CourtTime.Now&&!refunds.Any(r=>r.BookingId==b.Id)).ToList();
            return View(new CustomerAccountingPage(account,refunds,bookings));
        }
        catch(BookingRuleException ex){return BadRequest(ex.Message);}
    }
    [HttpGet]
    public IActionResult Export(DateOnly? from,DateOnly? to)
    {
        Response.Headers["Cache-Control"]="no-store";
        if(!ModelState.IsValid)return BadRequest();
        try
        {
            var page=store.Accounting(from??new DateOnly(CourtTime.Today.Year,CourtTime.Today.Month,1),to??CourtTime.Today,UserId);
            return File(AccountingCsv.Export(page,false),"text/csv; charset=utf-8",$"courtbook-my-account-{CourtTime.Iso(page.From)}-{CourtTime.Iso(page.To)}.csv");
        }
        catch(BookingRuleException ex){return BadRequest(ex.Message);}
    }
    [HttpPost]
    public IActionResult RequestRefund(RefundForm form)
    {
        if(!ModelState.IsValid)TempData["Error"]="เลือกรายการจองและระบุเหตุผล 5–500 ตัวอักษร";
        else try{store.RequestRefund(UserId,form);TempData["Success"]="ส่งคำขอคืนเงินแล้ว รอผู้ดูแลตรวจสอบและอนุมัติ";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return Redirect(Url.Action(nameof(Index))+"#refunds");
    }
}
