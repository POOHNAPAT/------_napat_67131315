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
        ViewData["DemoTopUpsEnabled"] = DemoTopUpsEnabled;
        return View("Index", store.Wallet(UserId));
    }
    [HttpGet] public IActionResult Index() => WalletView();

    [HttpPost]
    public IActionResult StartDemoPayment(DemoTopUpForm form)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        if (!ModelState.IsValid) return WalletView();
        try { return RedirectToAction(nameof(DemoPayment), new { id = store.StartDemoPayment(UserId, form.Amount) }); }
        catch (BookingRuleException ex) { ModelState.AddModelError("", ex.Message); return WalletView(); }
    }

    [HttpGet]
    public IActionResult DemoPayment(string id)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(id)) return BadRequest();
        var payment = store.FindDemoPayment(id, UserId);
        return payment is null ? NotFound() : View(payment);
    }

    [HttpPost]
    public IActionResult CompleteDemoPayment(string id, string outcome)
    {
        if (!DemoTopUpsEnabled) return NotFound();
        if (!ModelState.IsValid) return BadRequest();
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
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, RequestSizeLimit(6 * 1024 * 1024)]
    public IActionResult TopUp(TopUpForm form)
    {
        if (!ModelState.IsValid) return WalletView();
        try { store.SubmitTopUp(UserId,form); }
        catch(BookingRuleException ex) { ModelState.AddModelError("",ex.Message); return WalletView(); }
        TempData["Success"]="ส่งคำขอเติมเงินแล้ว ยอดจะเข้ากระเป๋าเมื่อผู้ดูแลตรวจสอบและอนุมัติ";
        return RedirectToAction(nameof(Index));
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
