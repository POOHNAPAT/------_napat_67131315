using System.Security.Claims;
using CourtBook.Models;
using CourtBook.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

[Authorize]
public class DamageController(BookingStore store):Controller
{
    private long Actor=>long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool Admin=>User.IsInRole("Admin");
    private IActionResult Reports()=>RedirectToAction(Admin?"DamageReports":"ReportDamage",Admin?"Admin":"Bookings");

    [HttpGet]
    public IActionResult Evidence(long id)
    {
        var image=store.DamageEvidenceImage(id,Actor,Admin);if(image is null)return NotFound();
        Response.Headers["Cache-Control"]="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";
        return File(image.Data,image.ContentType);
    }
    [HttpPost,RequestSizeLimit(6*1024*1024)]
    public IActionResult AddEvidence(long id,DamageEvidenceForm form)
    {
        if(!ModelState.IsValid){TempData["Error"]="กรุณาเลือกรูปและประเภทหลักฐานให้ถูกต้อง";return Reports();}
        try {if(!store.AddDamageEvidence(id,Actor,Admin,form))return NotFound();TempData["Success"]="เพิ่มหลักฐานแล้ว";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return Reports();
    }
    [HttpPost,Authorize(Roles="Admin")]
    public IActionResult Assess(long id,DamageAssessmentForm form)
    {
        if(!ModelState.IsValid){TempData["Error"]="กรุณาระบุผลตรวจสอบและยอดค่าซ่อมให้ถูกต้อง";return Reports();}
        try {if(!store.AssessDamage(id,Actor,form))return NotFound();TempData["Success"]="บันทึกผลตรวจสอบแล้ว ลูกค้าสามารถดูผลและตอบกลับได้";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return Reports();
    }
    [HttpPost,Authorize(Roles="Member")]
    public IActionResult Respond(long id,DamageResponseForm form)
    {
        if(!ModelState.IsValid){TempData["Error"]="กรุณาตรวจสอบคำตอบและเหตุผลขอทบทวน";return Reports();}
        try {if(!store.RespondToDamage(id,Actor,form)){TempData["Error"]="รายการเปลี่ยนแล้วหรือไม่พบรายงานของคุณ กรุณาตรวจสอบข้อมูลล่าสุด";return Reports();}TempData["Success"]="ส่งคำตอบแล้ว";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return Reports();
    }
    [HttpPost,Authorize(Roles="Member")]
    public IActionResult Pay(long id,int revision)
    {
        if(!ModelState.IsValid||revision<1)return BadRequest();
        try {if(!store.PayDamage(id,Actor,revision)){TempData["Error"]="รายการเปลี่ยนแล้วหรือไม่อยู่ในสถานะรอชำระ กรุณาตรวจสอบข้อมูลล่าสุด";return Reports();}TempData["Success"]="ชำระค่าซ่อมและออกใบเสร็จแล้ว";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return Reports();
    }
}
