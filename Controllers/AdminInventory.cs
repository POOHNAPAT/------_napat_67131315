using CourtBook.Models;
using Microsoft.AspNetCore.Mvc;

namespace CourtBook.Controllers;

public partial class AdminController
{
    [HttpGet]
    public IActionResult Inventory()=>View(store.Inventory());
    [HttpGet]
    public IActionResult EquipmentStock(string id)
    {
        var page=store.EquipmentStock(id);return page is null?NotFound():View(page);
    }
    [HttpPost]
    public IActionResult EquipmentStock(string id,EquipmentStockForm form)
    {
        if(!ModelState.IsValid){TempData["Error"]="ตรวจจำนวนสต็อกและระบุเหตุผลการปรับจำนวน";return RedirectToAction(nameof(EquipmentStock),new{id});}
        try{if(!store.SaveEquipmentStock(id,AdminId,form))return NotFound();TempData["Success"]="บันทึกสต็อกและประวัติแล้ว";}
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return RedirectToAction(nameof(EquipmentStock),new{id});
    }
    [HttpGet]
    public IActionResult EquipmentHandover(long id)
    {
        Response.Headers["Cache-Control"]="no-store";
        var page=store.EquipmentHandover(id);return page is null?NotFound():View("EquipmentHandover",page);
    }
    [HttpPost,RequestSizeLimit(6*1024*1024)]
    public IActionResult IssueEquipment(long id,EquipmentHandoverForm form)=>Handover(id,form,false);
    [HttpPost,RequestSizeLimit(6*1024*1024)]
    public IActionResult ReturnEquipment(long id,EquipmentHandoverForm form)=>Handover(id,form,true);
    private IActionResult Handover(long id,EquipmentHandoverForm form,bool returning)
    {
        if(!ModelState.IsValid){TempData["Error"]="ตรวจจำนวนและระบุสภาพอุปกรณ์ให้ถูกต้อง";return RedirectToAction(nameof(EquipmentHandover),new{id});}
        try
        {
            if(!store.RecordEquipmentHandover(id,AdminId,form,returning))return NotFound();
            TempData["Success"]=returning?"บันทึกรับคืนแล้ว หากชำรุดจะพักอุปกรณ์และเปิดรายงานให้ตรวจสอบต่อ":"บันทึกส่งมอบอุปกรณ์แล้ว";
        }
        catch(BookingRuleException ex){TempData["Error"]=ex.Message;}
        return RedirectToAction(nameof(EquipmentHandover),new{id});
    }
    [HttpGet]
    public IActionResult EquipmentMovementImage(long id)
    {
        var image=store.EquipmentMovementImage(id);if(image is null)return NotFound();
        Response.Headers["Cache-Control"]="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";return File(image.Data,image.ContentType);
    }
}
