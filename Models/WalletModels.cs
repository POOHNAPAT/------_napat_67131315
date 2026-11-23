using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class TopUpForm : PaymentSubmissionForm
{
    [Range(typeof(decimal), "1", "100000", ErrorMessage = "เติมเงินได้ครั้งละ 1–100,000 บาท")]
    public decimal Amount { get; set; }
}

public class PlanForm
{
    [Required] public string Id { get; set; } = "";
    [Range(typeof(decimal), "1", "1000000", ErrorMessage = "ราคาแพ็กต้องอยู่ระหว่าง 1–1,000,000 บาท")]
    public decimal Price { get; set; }
    [Range(1, 100, ErrorMessage = "ส่วนลดต้องอยู่ระหว่าง 1–100%")]
    public int DiscountPercent { get; set; }
    public bool IsActive { get; set; }
}

public record MembershipPlan(string Id, string Name, int Months, long PriceSatang, int DiscountPercent, bool IsActive);
public record Membership(long Id, string PlanId, string Name, DateOnly Starts, DateOnly Ends, int DiscountPercent);
public record WalletEntry(long Id, long AmountSatang, string Description, string CreatedAt);
public record TopUp(long Id, long UserId, string MemberName, long AmountSatang, string TransferReference,
    string Status, string CreatedAt, string ReviewNote)
{
    public string StatusLabel => Status switch { "Approved" => "เติมเงินสำเร็จ", "Rejected" => "ไม่อนุมัติ", _ => "รอตรวจสอบ" };
}
public record WalletPage(long BalanceSatang, IReadOnlyList<WalletEntry> Entries, IReadOnlyList<TopUp> TopUps,
    IReadOnlyList<MembershipPlan> Plans, Membership? Membership);

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
        "Failed" => "จำลองการชำระเงินไม่สำเร็จ",
        "Cancelled" => "ยกเลิกรายการแล้ว",
        _ => IsExpired ? "รายการหมดอายุ" : "รอจำลองการชำระเงิน"
    };
}
