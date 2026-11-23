using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class ExpenseForm
{
    public DateOnly Date { get; set; }=CourtTime.Today;
    [Required] public string Category { get; set; }="Maintenance";
    [Required,StringLength(500,MinimumLength=5)] public string Description { get; set; }="";
    [Required,StringLength(150)] public string Payee { get; set; }="";
    [Range(typeof(decimal),"0.01","1000000")] public decimal Amount { get; set; }
    [Required] public string Method { get; set; }="Transfer";
    [Required,StringLength(200)] public string Reference { get; set; }="";
    public static readonly string[] Categories=["Maintenance","Utilities","Equipment","Staff","Other"];
    public static string Label(string category)=>category switch{"Maintenance"=>"ซ่อมบำรุง","Utilities"=>"ค่าน้ำ / ค่าไฟ","Equipment"=>"ซื้ออุปกรณ์","Staff"=>"บุคลากร","Other"=>"อื่น ๆ",_=>category};
}
public record Expense(long Id,DateOnly Date,string Category,string Description,string Payee,long AmountSatang,string Method,string Reference,string Status,string CreatedBy,string CreatedAt,string VoidReason,string? VoidedAt,string VoidedBy);
public record ExpensePage(ExpenseForm Form,IReadOnlyList<Expense> Expenses,DateOnly? From=null,DateOnly? To=null);
public class RefundForm
{
    [Range(1,long.MaxValue)] public long BookingId { get; set; }
    [Required,StringLength(500,MinimumLength=5)] public string Reason { get; set; }="";
}
public record BookingRefund(long Id,long BookingId,long UserId,string MemberName,string MemberEmail,string CourtName,long AmountSatang,string Reason,string Status,string RequestedAt,string? DecidedAt,string AdminNote,string DecidedBy)
{
    public string Reference=>$"RF{Id:000000}";
    public string StatusLabel=>Status switch{"Pending"=>"รอตรวจสอบ","Approved"=>"คืนเข้ากระเป๋าแล้ว","Rejected"=>"ไม่อนุมัติ",_=>Status};
}
public record RefundPage(IReadOnlyList<BookingRefund> Refunds,string? Status);
public record AccountingEntry(string Reference,string OccurredAt,string Kind,string Description,string MemberName,long AmountSatang,string Status,string Method,string? ReceiptId=null)
{
    public DateOnly Date=>DateOnly.FromDateTime(DateTimeOffset.Parse(OccurredAt).ToOffset(TimeSpan.FromHours(7)).DateTime);
    public string KindLabel=>Kind switch{"Booking"=>"ชำระค่าจอง","Membership"=>"ซื้อแพ็ก","TopUp"=>"เติมเงินผ่านธนาคาร","Demo"=>"เครดิตทดลอง","Refund"=>"คืนค่าจองเข้ากระเป๋า","Expense"=>"รายจ่าย",_=>Kind};
}
public record AccountingPage(DateOnly From,DateOnly To,IReadOnlyList<AccountingEntry> Entries,long WalletBalanceSatang)
{
    private long Sum(string kind)=>Entries.Where(e=>e.Kind==kind&&e.Status!="Void").Sum(e=>e.AmountSatang);
    public long BookingSatang=>Sum("Booking");
    public long MembershipSatang=>Sum("Membership");
    public long TopUpSatang=>Sum("TopUp");
    public long DemoSatang=>Sum("Demo");
    public long RefundSatang=>Sum("Refund");
    public long ExpenseSatang=>Sum("Expense");
    public long NetSalesSatang=>BookingSatang+MembershipSatang-RefundSatang;
    public long ExternalInSatang=>TopUpSatang+Entries.Where(e=>e.Kind=="Booking"&&e.Method!="Wallet").Sum(e=>e.AmountSatang);
    public long CashDifferenceSatang=>ExternalInSatang-ExpenseSatang;
}
public record CustomerAccountingPage(AccountingPage Account,IReadOnlyList<BookingRefund> Refunds,IReadOnlyList<Booking> RefundableBookings);
