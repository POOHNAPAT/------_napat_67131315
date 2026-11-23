using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class DamageAssessmentForm
{
    [Range(0,int.MaxValue)] public int Revision { get; set; }
    public bool Waive { get; set; }
    [Range(typeof(decimal),"0","1000000")] public decimal Amount { get; set; }
    [Required,StringLength(2000,MinimumLength=5)] public string Reason { get; set; } = "";
}
public class DamageResponseForm
{
    [Range(1,int.MaxValue)] public int Revision { get; set; }
    [Required,RegularExpression("^(Accept|Dispute)$")] public string Decision { get; set; } = "";
    [StringLength(2000)] public string? Note { get; set; }
}
public class DamageEvidenceForm
{
    [Required] public IFormFile? Image { get; set; }
    [Required,RegularExpression("^(Before|After|Invoice|Other)$")] public string Kind { get; set; } = "After";
    [StringLength(300)] public string? Caption { get; set; }
}
public record DamageEvidence(long Id,string Kind,string Caption,string UploadedBy,string CreatedAt)
{
    public static string Label(string kind)=>kind switch {"Before"=>"สภาพก่อนใช้งาน","After"=>"สภาพหลังใช้งาน","Invoice"=>"ใบเสนอราคา / ค่าซ่อม",_=>"หลักฐานเพิ่มเติม"};
}
public record DamageClaim(long ReportId,int Revision,string Status,long AmountSatang,string Reason,string CustomerNote,string? PaidAt)
{
    public static readonly string[] Statuses = ["AwaitingCustomer","Disputed","AwaitingPayment","Paid","Waived"];
    public static string Label(string status)=>status switch {"AwaitingCustomer"=>"รอลูกค้าตอบกลับ","Disputed"=>"ขอทบทวน","AwaitingPayment"=>"รอชำระค่าซ่อม","Paid"=>"ชำระแล้ว / ปิดเรื่อง","Waived"=>"ไม่เรียกเก็บ / ปิดเรื่อง",_=>DamageReport.Label(status)};
    public bool Closed=>Status is "Paid" or "Waived";
    public string ReceiptId=>$"RC-DM-{ReportId:000000}";
}
public record DamageClaimEvent(int Revision,string Status,long AmountSatang,string Note,string ActorName,string CreatedAt);
public record AdminDamageReportPage(Booking Booking,DamageReportForm Form);
