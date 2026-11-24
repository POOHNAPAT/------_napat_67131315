using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class TopUpForm : PaymentSubmissionForm
{
    [Range(typeof(decimal), "0.01", "100000", ErrorMessage = "เติมเงินได้ครั้งละ 0.01–100,000 บาท")]
    public decimal Amount { get; set; }
    public string CheckoutPath { get; set; } = "";
}

public class PlanForm
{
    [Range(1,12)] public int? Months { get; set; }
    [Required] public string Id { get; set; } = "";
    [Range(typeof(decimal), "1", "1000000", ErrorMessage = "ราคาแพ็กต้องอยู่ระหว่าง 1–1,000,000 บาท")]
    public decimal Price { get; set; }
    [Range(1, 100, ErrorMessage = "ส่วนลดต้องอยู่ระหว่าง 1–100%")]
    public int DiscountPercent { get; set; }
    [Range(0,100)] public int MonthlyHours { get; set; }
    public long QuotaSportTypeId { get; set; }
    public bool IsActive { get; set; }
}

public record MembershipPlan(string Id, string Name, int Months, long PriceSatang, int DiscountPercent, bool IsActive,int MonthlyHours=0,long QuotaSportTypeId=0,string QuotaSportName="");
public record Membership(long Id, string PlanId, string Name, DateOnly Starts, DateOnly Ends, int DiscountPercent,int MonthlyHours=0,long QuotaSportTypeId=0,string QuotaSportName="",IReadOnlyList<QuotaBalance>? Quotas=null);
public record QuotaBalance(DateOnly Starts,DateOnly Ends,int TotalHours,int UsedHours){public int RemainingHours=>Math.Max(0,TotalHours-UsedHours);}
public record WalletEntry(long Id, long AmountSatang, string Description, string CreatedAt);
public record TopUp(long Id, long UserId, string MemberName, long AmountSatang, string TransferReference,
    string Status, string CreatedAt, string ReviewNote,string CheckoutPath="")
{
    public string StatusLabel => Status switch { "Approved" => "เติมเงินสำเร็จ", "Rejected" => "ไม่อนุมัติ", _ => "รอตรวจสอบ" };
}
public record WalletPage(long BalanceSatang, IReadOnlyList<WalletEntry> Entries, IReadOnlyList<TopUp> TopUps,
    IReadOnlyList<MembershipPlan> Plans, Membership? Membership);

public record WalletCheckout(long? BookingId,string? PlanId,long? DamageId,string Title,string ReturnUrl,long RequiredSatang)
{
    public long MissingSatang(long balance)=>Math.Max(0,RequiredSatang-balance);
    public decimal SuggestedAmount(long balance)=>Math.Min(100000m,MissingSatang(balance)/100m);
    public static bool SafePath(string path)=>System.Text.RegularExpressions.Regex.IsMatch(path,@"^(/Bookings/Result/[0-9]+|/Home/Membership|/Bookings/ReportDamage#report-[0-9]+)$");
}

public class DemoTopUpForm
{
    [Range(typeof(decimal), "1", "100000", ErrorMessage = "เติมเครดิตทดลองได้ครั้งละ 1–100,000 บาท")]
    public decimal Amount { get; set; }
}

public record DemoPayment(string Id, long AmountSatang, string Status, string CreatedAt)
{
    public bool IsExpired => DateTimeOffset.Parse(CreatedAt).AddMinutes(15) < DateTimeOffset.UtcNow;
    public string StatusLabel => Status switch
    {
        "Paid" => "เติมเครดิตทดลองสำเร็จ",
        "Failed" => "การชำระเงินไม่สำเร็จ",
        "Cancelled" => "ยกเลิกรายการแล้ว",
        _ => IsExpired ? "รายการหมดอายุ" : "รอผลการชำระเงินทดลอง"
    };
}

public record ReceiptLine(string Name,int Quantity,string Unit,long PriceSatang)
{
    public long TotalSatang=>Quantity*PriceSatang;
}
public record ReceiptSnapshot(string Kind,string MemberName,string MemberEmail,string SourceReference,string PaidAt,string PaymentReference,
    IReadOnlyList<ReceiptLine> Lines,long GrossSatang,long DiscountSatang,long NetSatang,string PromotionName,
    string CourtName="",string SportTypeName="",DateOnly? PlayDate=null,string TimeLabel="",
    string PlanName="",DateOnly? Starts=null,DateOnly? Ends=null,int DiscountPercent=0,int MonthlyHours=0,string QuotaSportName="",int IncludedHours=0);
public record PaymentReceipt(string Id,long? BookingId,long? MembershipId,ReceiptSnapshot Data,string CreatedAt,Booking? Booking=null,BookingRefund? Refund=null)
{
    public string Title=>Data.Kind=="Damage"?"ใบเสร็จค่าซ่อม / ค่าเสียหาย":Data.Kind=="Booking"&&Data.NetSatang==0&&Data.IncludedHours>0?"หลักฐานใช้สิทธิ์แพ็กสนาม":Data.Kind=="Booking"?"ใบเสร็จค่าจองสนาม":"ใบเสร็จซื้อแพ็กสมาชิก";
    public string EntryStatus=>Booking is null?"":Booking.CheckOut is not null?"เช็กเอาต์แล้ว · รับคืนอุปกรณ์ครบ":Booking.ApprovalStatus=="Cancelled"?"ยกเลิกการจองแล้ว":Booking.ApprovalStatus=="Pending"?"รอผู้ดูแลยืนยันการจอง":Booking.Status=="Completed"?Booking.StatusLabel:Data.IncludedHours>0&&Data.NetSatang==0?"ใช้สิทธิ์แพ็กและยืนยันการจองแล้ว":"ชำระเงินและยืนยันการจองแล้ว";
}
