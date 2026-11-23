using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace CourtBook.Models;

public static class CourtTime
{
    public static DateTime Now => DateTime.UtcNow.AddHours(7);
    public static DateOnly Today => DateOnly.FromDateTime(Now);
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Display(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("th-TH"));
    public static string Money(long satang) => (satang / 100m).ToString("N2");
    public static string Hour(int hour) => $"{hour:00}:00";
    public static IEnumerable<int> Hours => Enumerable.Range(8, 14);
}

public class SiteUser
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Member";
}

public class RegisterForm
{
    [Required(ErrorMessage = "กรุณากรอกชื่อ"), StringLength(100)] public string Name { get; set; } = "";
    [Required(ErrorMessage = "กรุณากรอกอีเมล"), EmailAddress(ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง"), StringLength(150)] public string Email { get; set; } = "";
    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน"), StringLength(128, MinimumLength = 8, ErrorMessage = "รหัสผ่านต้องมี 8–128 ตัวอักษร"), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required(ErrorMessage = "กรุณายืนยันรหัสผ่าน"), Compare(nameof(Password), ErrorMessage = "รหัสผ่านไม่ตรงกัน"), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class LoginForm
{
    [Required(ErrorMessage = "กรุณากรอกอีเมล"), EmailAddress] public string Email { get; set; } = "";
    [Required(ErrorMessage = "กรุณากรอกรหัสผ่าน"), DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class Court
{
    public string ImageVersion { get; set; } = "";
    public string? ImageUrl => ImageVersion.Length == 0 ? null : $"/Courts/Image/{Id}?v={ImageVersion}";
    public long SportTypeId { get; set; } = 1;
    public string SportTypeName { get; set; } = "";
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long PriceSatang { get; set; }
    public bool IsActive { get; set; }
}

public class CourtForm
{
    public IFormFile? Image { get; set; }
    public bool RemoveImage { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] public string? CurrentImageUrl { get; set; }
    [Range(1, long.MaxValue, ErrorMessage = "กรุณาเลือกประเภทกีฬา")] public long SportTypeId { get; set; } = 1;
    public long Id { get; set; }
    [Required(ErrorMessage = "กรุณากรอกชื่อสนาม"), StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    [Range(typeof(decimal), "1", "100000", ErrorMessage = "ราคาต้องอยู่ระหว่าง 1–100,000 บาท")] public decimal Price { get; set; } = 200;
    public bool IsActive { get; set; } = true;
}

public class Promotion
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Value100 { get; set; }
    public int MinHours { get; set; }
    public int MinDays { get; set; }
    public long MinSpendSatang { get; set; }
    public DateOnly Starts { get; set; }
    public DateOnly Ends { get; set; }
    public bool IsActive { get; set; }
    public string DiscountLabel => Kind == "Percent" ? $"ลด {Value100 / 100m:0.##}%" : $"ลด {CourtTime.Money(Value100)} บาท";
    public string Condition => Rule switch
    {
        "NewMember" => "จองครั้งแรกของบัญชี",
        "Consecutive" => $"จองต่อเนื่องอย่างน้อย {MinHours} ชั่วโมง",
        "WeekdayMorning" => "ทุกชั่วโมงอยู่ใน 08:00–12:00 น. วันจันทร์–ศุกร์",
        "Advance" => $"จองล่วงหน้าอย่างน้อย {MinDays} วันตามปฏิทิน",
        "Threshold" => $"ยอดก่อนส่วนลดตั้งแต่ {CourtTime.Money(MinSpendSatang)} บาท",
        _ => "ไม่รองรับเงื่อนไขนี้"
    };
}

public class PromotionForm
{
    public long Id { get; set; }
    [Required(ErrorMessage = "กรุณากรอกชื่อโปรโมชั่น"), StringLength(100)] public string Name { get; set; } = "";
    [Required] public string Rule { get; set; } = "NewMember";
    [Required] public string Kind { get; set; } = "Fixed";
    [Range(typeof(decimal), "0.01", "100000", ErrorMessage = "ส่วนลดต้องมากกว่า 0 และไม่เกิน 100,000")] public decimal Value { get; set; } = 50;
    [Range(1, 14)] public int MinHours { get; set; } = 3;
    [Range(1, 3650)] public int MinDays { get; set; } = 7;
    [Range(typeof(decimal), "1", "1000000")] public decimal MinSpend { get; set; } = 1000;
    public DateOnly Starts { get; set; } = CourtTime.Today;
    public DateOnly Ends { get; set; } = CourtTime.Today.AddYears(1);
    public bool IsActive { get; set; } = true;
}

public record Slot(int Hour, string Status)
{
    public string Label => $"{CourtTime.Hour(Hour)}–{CourtTime.Hour(Hour + 1)}";
    public string StatusLabel => Status switch { "Available" => "ว่าง", "Booked" => "มีการจอง", "Past" => "ผ่านไปแล้ว", _ => "ปิดให้จอง" };
}
public record CourtSchedule(Court Court, IReadOnlyList<Slot> Slots);
public record SchedulePage(DateOnly Date, long? CourtId, IReadOnlyList<CourtSchedule> Courts);

public class BookingForm
{
    [Range(1, long.MaxValue)] public long CourtId { get; set; }
    public DateOnly Date { get; set; } = CourtTime.Today.AddDays(1);
    public int[] Hours { get; set; } = [];
    public long? PromotionId { get; set; }
}
public record BookingPage(BookingForm Form, IReadOnlyList<Court> Courts, IReadOnlyList<Slot> Slots, IReadOnlyList<Promotion> Promotions);
public record PromotionOption(long Id, string Name, string DiscountLabel, bool Eligible, string Condition);
public record Quote(long GrossSatang, long DiscountSatang, long NetSatang, IReadOnlyList<PromotionOption> Promotions, string? MembershipName = null);

public class Booking
{
    public string? PaymentSubmittedAt { get; set; }
    public string TransferReference { get; set; } = "";
    public string ApprovalStatus { get; set; } = "Pending";
    public string? CancelledAt { get; set; }
    public bool CanCancel => ApprovalStatus is "Pending" or "Confirmed" && PaidAt is null && PaymentSubmittedAt is null
        && Date.ToDateTime(new TimeOnly(StartHour, 0)) > CourtTime.Now;
    public string? ConfirmedAt { get; set; }
    public string ConfirmedByName { get; set; } = "";
    public string? PaidAt { get; set; }
    public string PaidByName { get; set; } = "";
    public string PaymentReference { get; set; } = "";
    public string PaymentStatusLabel => PaidAt is not null ? "ยืนยันชำระเงินแล้ว" : ApprovalStatus == "Cancelled" ? "ไม่มีการเรียกเก็บเงิน" : PaymentSubmittedAt is not null ? "รอตรวจสอบการชำระเงิน" : "รอชำระเงิน";
    public long Id { get; set; }
    public long UserId { get; set; }
    public string MemberName { get; set; } = "";
    public string MemberEmail { get; set; } = "";
    public long CourtId { get; set; }
    public string CourtName { get; set; } = "";
    public DateOnly Date { get; set; }
    public int StartHour { get; set; }
    public int Hours { get; set; }
    public long PriceSatang { get; set; }
    public long GrossSatang { get; set; }
    public long DiscountSatang { get; set; }
    public long NetSatang { get; set; }
    public string PromotionName { get; set; } = "";
    public string Reference => $"CB{Id:000000}";
    public string TimeLabel => $"{CourtTime.Hour(StartHour)}–{CourtTime.Hour(StartHour + Hours)}";
    public string Status => ApprovalStatus is "Pending" or "Cancelled" ? ApprovalStatus : Date.ToDateTime(TimeOnly.MinValue).AddHours(StartHour + Hours) <= CourtTime.Now ? "Completed" : "Confirmed";
    public string StatusLabel => Status switch { "Cancelled" => "ยกเลิกแล้ว", "Pending" => "รอผู้ดูแลยืนยัน", "Completed" => "เสร็จสิ้น", _ => "ยืนยันแล้ว" };
}
public record SportType(long Id, string Name);
public record CourtImage(byte[] Data, string ContentType);
public class SportTypeForm
{
    [Required(ErrorMessage = "กรุณากรอกประเภทกีฬา"), StringLength(100)] public string Name { get; set; } = "";
}
public class PaymentConfirmationForm
{
    [Required(ErrorMessage = "กรุณาระบุเลขอ้างอิงหรือรายละเอียดการรับเงิน"), StringLength(200)]
    public string Reference { get; set; } = "";
}
public class PaymentSubmissionForm
{
    [Required(ErrorMessage = "กรุณาระบุเลขรายการโอน"), StringLength(200)] public string TransferReference { get; set; } = "";
    [Required(ErrorMessage = "กรุณาแนบรูปสลิปโอนเงิน")] public IFormFile? Slip { get; set; }
}
public record CustomerAccount(long Id, string Name, string Email, int Bookings);
public record CustomerPage(CustomerAccount Account, IReadOnlyList<Booking> Bookings);
public record BookingListPage(IReadOnlyList<Booking> Bookings, DateOnly? Date, string? Status, long? CourtId, IReadOnlyList<Court> Courts);
public record AdminOverview(int Courts, int Bookings, int Promotions, IReadOnlyList<Booking> Recent);
public record AdminSchedulePage(Court Court, DateOnly Date, IReadOnlyList<Court> Courts, IReadOnlyList<Slot> Slots);
public class BookingRuleException(string message) : Exception(message);
