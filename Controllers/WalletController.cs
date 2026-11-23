using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize(Roles="Member")]
public class WalletController(BookingStore store, IConfiguration configuration) : Controller
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool DemoTopUpsEnabled => configuration.GetValue<bool>("Demo:AutomaticTopUps");
    private IActionResult WalletView()
    {
        Response.Headers["Cache-Control"]="no-store";
        ViewData["DemoTopUpsEnabled"] = DemoTopUpsEnabled;
        return View("Index", store.Wallet(UserId));
    }
    private IActionResult? PrepareCheckout(long? bookingId,string? planId,long? damageId)
    {
        if((bookingId.HasValue?1:0)+(!string.IsNullOrEmpty(planId)?1:0)+(damageId.HasValue?1:0)>1)return BadRequest();
        WalletCheckout? checkout=null;
        if(bookingId.HasValue)
        {
            var booking=store.Bookings(UserId).SingleOrDefault(b=>b.Id==bookingId);if(booking is null)return NotFound();
            var amount=booking.ApprovalStatus is "Pending" or "Confirmed"&&booking.PaidAt is null&&booking.PaymentSubmittedAt is null?booking.NetSatang:0;
            checkout=new(bookingId,null,null,"ค่าจอง "+booking.Reference,$"/Bookings/Result/{booking.Id}",amount);
        }
        else if(!string.IsNullOrEmpty(planId))
        {
            var plan=store.Plans().SingleOrDefault(p=>p.Id==planId);if(plan is null)return NotFound();
            checkout=new(null,planId,null,plan.Name,"/Home/Membership",plan.IsActive?plan.PriceSatang:0);
        }
        else if(damageId.HasValue)
        {
            var report=store.DamageReports(UserId).SingleOrDefault(r=>r.Id==damageId);if(report is null)return NotFound();
            checkout=new(null,null,damageId,"ค่าซ่อม "+report.Reference,$"/Bookings/ReportDamage#report-{report.Id}",report.Claim?.Status=="AwaitingPayment"?report.Claim.AmountSatang:0);
        }
        ViewData["Checkout"]=checkout;return null;
    }
    private IActionResult ReturnToWallet(long? bookingId,string? planId,long? damageId)=>RedirectToAction(nameof(Index),new{bookingId,planId,damageId});
    [HttpGet]
    public IActionResult Index(long? bookingId,string? planId,long? damageId)
    {
        if(!ModelState.IsValid)return BadRequest();
        return PrepareCheckout(bookingId,planId,damageId)??WalletView();
    }

    [HttpPost]
    public IActionResult StartDemoPayment(DemoTopUpForm form,long? bookingId,string? planId,long? damageId)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        var invalid=PrepareCheckout(bookingId,planId,damageId);if(invalid is not null)return invalid;
        if (!ModelState.IsValid) return WalletView();
        try { return RedirectToAction(nameof(DemoPayment), new { id = store.StartDemoPayment(UserId, form.Amount),bookingId,planId,damageId }); }
        catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); return WalletView(); }
    }

    [HttpGet]
    public IActionResult DemoPayment(string id,long? bookingId,string? planId,long? damageId)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(id)) return BadRequest();
        var invalid=PrepareCheckout(bookingId,planId,damageId);if(invalid is not null)return invalid;
        var payment = store.FindDemoPayment(id, UserId);
        return payment is null ? NotFound() : View(payment);
    }

    [HttpPost]
    public IActionResult CompleteDemoPayment(string id, string outcome,long? bookingId,string? planId,long? damageId)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        if (!ModelState.IsValid) return BadRequest();
        var invalid=PrepareCheckout(bookingId,planId,damageId);if(invalid is not null)return invalid;
        if (store.FindDemoPayment(id, UserId) is null) return NotFound();
        try
        {
            if (!store.CompleteDemoPayment(id, UserId, outcome))
                TempData["Error"] = "รายการนี้เสร็จสิ้นหรือหมดอายุแล้ว ไม่มีการเพิ่มเครดิตซ้ำ";
            else if (outcome == "Paid")
                TempData["Success"] = "เติมเครดิตทดลองเข้ากระเป๋าอัตโนมัติแล้ว ไม่มีการเรียกเก็บเงินจริง";
            else
                TempData["Error"] = "การชำระเงินทดลองไม่สำเร็จหรือถูกยกเลิก ไม่มีการเพิ่มเครดิต";
        }
        catch (BookingRuleException ex) { TempData["Error"] = ex.Message; }
        return ReturnToWallet(bookingId,planId,damageId);
    }

    [HttpPost, RequestSizeLimit(6 * 1024 * 1024)]
    public IActionResult TopUp(TopUpForm form,long? bookingId,string? planId,long? damageId)
    {
        var invalid=PrepareCheckout(bookingId,planId,damageId);if(invalid is not null)return invalid;
        form.CheckoutPath=(ViewData["Checkout"] as WalletCheckout)?.ReturnUrl??"";
        ViewData["TopUpAmount"]=form.Amount;
        if (!ModelState.IsValid) return WalletView();
        try { store.SubmitTopUp(UserId,form); }
        catch(BookingRuleException ex) { ModelState.AddModelError("",ex.Message); return WalletView(); }
        TempData["Success"]="ส่งคำขอเติมเงินแล้ว ยอดจะเข้ากระเป๋าเมื่อผู้ดูแลตรวจสอบและอนุมัติ";
        return ReturnToWallet(bookingId,planId,damageId);
    }

    [HttpGet]
    public IActionResult Slip(long id)
    {
        var image=store.TopUpSlip(id,UserId);
        if(image is null) return NotFound();
        Response.Headers["Cache-Control"]="no-store";
        Response.Headers["X-Content-Type-Options"]="nosniff";
        return File(image.Data,image.ContentType);
    }

    [HttpPost]
    public IActionResult Buy(string planId, long expectedPriceSatang, int expectedDiscountPercent, int? expectedMonths,int expectedMonthlyHours=0,long expectedQuotaSportTypeId=0)
    {
        if(!ModelState.IsValid)return BadRequest();
        try { var id=store.BuyPlan(UserId,planId,expectedPriceSatang,expectedDiscountPercent,expectedMonths,expectedMonthlyHours,expectedQuotaSportTypeId); TempData["Success"]="ซื้อแพ็กสำเร็จ ออกใบเสร็จแล้ว"; return RedirectToAction("Details","Receipts",new{id=$"RC-MB-{id:000000}"}); }
        catch(BookingRuleException ex) { TempData["Error"]=ex.Message; }
        return RedirectToAction("Membership","Home");
    }
}
