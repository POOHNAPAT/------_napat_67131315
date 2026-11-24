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
    public string Location { get; set; } = "";
    public decimal? Rating { get; set; }
    public int ReviewCount { get; set; }
    public string SportImage => SportsAssets.Court(SportTypeName);
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
    public int MinPlayers { get; set; } = 4;
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
        "Group" => $"จองเป็นกลุ่มตั้งแต่ {MinPlayers} คน",
        "Consecutive" => $"จองต่อเนื่องอย่างน้อย {MinHours} ชั่วโมง",
        "WeekdayMorning" => "ทุกชั่วโมงอยู่ใน 08:00–12:00 น. วันจันทร์–ศุกร์",
        "Advance" => $"จองล่วงหน้าอย่างน้อย {MinDays} วันตามปฏิทิน",
        "Threshold" => $"ยอดก่อนส่วนลดตั้งแต่ {CourtTime.Money(MinSpendSatang)} บาท",
        _ => "ไม่รองรับเงื่อนไขนี้"
    };
}

public class PromotionForm
{
    [Range(2,100)] public int MinPlayers { get; set; } = 4;
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
    public int? ExpectedIncludedHours { get; set; }
    [Range(1,100)] public int Players { get; set; } = 1;
    [Range(1, long.MaxValue)] public long CourtId { get; set; }
    public DateOnly Date { get; set; } = CourtTime.Today.AddDays(1);
    public int[] Hours { get; set; } = [];
    public long? PromotionId { get; set; }
    public Dictionary<string, int> Equipment { get; set; } = [];
}
public record RentalEquipment(string Id, string Name, string Unit, long PriceSatang, long SportTypeId, string SportTypeName, bool IsActive, string ImageUrl = "",int StockQuantity=0,int RepairQuantity=0);
public record BookingEquipment(string Id, string Name, string Unit, long PriceSatang, int Quantity)
{
    public long TotalSatang => PriceSatang * Quantity;
}
public class EquipmentForm
{
    [Range(0,1000000)] public int InitialStock { get; set; }
    [StringLength(500)] public string? ImageUrl { get; set; }
    public string? Id { get; set; } = "";
    [Range(1, long.MaxValue, ErrorMessage = "กรุณาเลือกประเภทกีฬา")] public long SportTypeId { get; set; } = 1;
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(30)] public string Unit { get; set; } = "ชิ้น";
    [Range(typeof(decimal), "0.01", "100000", ErrorMessage = "ราคาต้องอยู่ระหว่าง 0.01–100,000 บาท")] public decimal Price { get; set; } = 50;
    public bool IsActive { get; set; } = true;
}
public record BookingPage(BookingForm Form, IReadOnlyList<Court> Courts, IReadOnlyList<Slot> Slots, IReadOnlyList<Promotion> Promotions, IReadOnlyList<RentalEquipment> Equipment);
public record PromotionOption(long Id, string Name, string DiscountLabel, bool Eligible, string Condition);
public record Quote(long GrossSatang, long DiscountSatang, long NetSatang, IReadOnlyList<PromotionOption> Promotions, string? MembershipName = null, long EquipmentSatang = 0, IReadOnlyList<BookingEquipment>? Equipment = null,int IncludedHours=0,long? QuotaMembershipId=null,string QuotaPeriod="",int RemainingQuotaHours=0);

public class Booking
{
    public BookingCheckIn? CheckIn { get; set; }
    public BookingCheckOut? CheckOut { get; set; }
    public int OutstandingEquipment { get; set; }
    public bool CanCheckOut=>CheckIn is not null&&CheckOut is null&&ApprovalStatus=="Confirmed"&&EntryPaymentComplete&&OutstandingEquipment==0;
    public DateTime Starts=>Date.ToDateTime(new TimeOnly(StartHour,0));
    public DateTime Ends=>Starts.AddHours(Hours);
    public bool EntryPaymentComplete=>PaidAt is not null||NetSatang==0;
    public bool CanPayWallet=>(ApprovalStatus is "Pending" or "Confirmed")&&PaidAt is null&&PaymentSubmittedAt is null&&NetSatang>0&&Ends>CourtTime.Now;
    public bool HasEntryPass=>CheckOut is null&&ApprovalStatus=="Confirmed"&&EntryPaymentComplete&&Ends>CourtTime.Now;
    public string? CheckInBlockReason(DateTime now)=>CheckOut is not null?"รายการนี้เช็กเอาต์แล้ว ไม่สามารถเช็กอินซ้ำได้":CheckIn is not null?"รายการนี้เช็กอินแล้ว":ApprovalStatus=="Cancelled"?"รายการนี้ยกเลิกแล้ว":ApprovalStatus!="Confirmed"?"รอผู้ดูแลยืนยันการจอง":!EntryPaymentComplete?"รายการนี้ยังไม่ชำระเงินครบ":Date!=DateOnly.FromDateTime(now)?"เช็กอินได้เฉพาะวันที่จอง":now<Starts.AddMinutes(-15)?"ยังไม่ถึงเวลาเช็กอิน เปิดก่อนเริ่มเล่น 15 นาที":now>=Ends?"พ้นช่วงเวลาใช้งานแล้ว":null;
    public int IncludedHours { get; set; }
    public int Players { get; set; } = 1;
    public string SportTypeName { get; set; } = "";
    public IReadOnlyList<BookingEquipment> Equipment { get; set; } = [];
    public long EquipmentSatang => Equipment.Sum(e => e.TotalSatang);
    public string? PaymentSubmittedAt { get; set; }
    public string TransferReference { get; set; } = "";
    public string ApprovalStatus { get; set; } = "Pending";
    public string? CancelledAt { get; set; }
    public bool CanCancel => CheckIn is null && (ApprovalStatus is "Pending" or "Confirmed") && (PaidAt is null || (NetSatang==0 && IncludedHours>0)) && PaymentSubmittedAt is null
        && Date.ToDateTime(new TimeOnly(StartHour, 0)) > CourtTime.Now;
    public string? ConfirmedAt { get; set; }
    public string ConfirmedByName { get; set; } = "";
    public string? PaidAt { get; set; }
    public string PaidByName { get; set; } = "";
    public string PaymentReference { get; set; } = "";
    public string PaymentStatusLabel => PaidAt is not null && NetSatang==0 && IncludedHours>0 ? "ใช้สิทธิ์แพ็กแล้ว" : PaidAt is not null ? "ยืนยันชำระเงินแล้ว" : ApprovalStatus == "Cancelled" ? "ไม่มีการเรียกเก็บเงิน" : PaymentSubmittedAt is not null ? "รอตรวจสอบการชำระเงิน" : "รอชำระเงิน";
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
    public string Status => ApprovalStatus is "Pending" or "Cancelled" ? ApprovalStatus : CheckOut is not null||Ends<=CourtTime.Now ? "Completed" : "Confirmed";
    public string StatusLabel => CheckOut is not null?"เช็กเอาต์แล้ว":Status=="Completed"&&OutstandingEquipment>0?"หมดเวลา · รอคืนอุปกรณ์":Status=="Completed"&&CheckIn is not null?"หมดเวลา · รอเช็กเอาต์":Status switch { "Cancelled" => "ยกเลิกแล้ว", "Pending" => "รอผู้ดูแลยืนยัน", "Completed" => "เสร็จสิ้น", _ => "ยืนยันแล้ว" };
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
    [StringLength(200)] public string? TransferReference { get; set; }
    [Required(ErrorMessage = "กรุณาแนบรูปสลิปโอนเงิน")] public IFormFile? Slip { get; set; }
}
public record CustomerAccount(long Id, string Name, string Email, int Bookings);
public record CustomerPage(CustomerAccount Account, IReadOnlyList<Booking> Bookings);
public record BookingListPage(IReadOnlyList<Booking> Bookings, DateOnly? Date, string? Status, long? CourtId, IReadOnlyList<Court> Courts);
public record AdminOverview(int Courts, int Bookings, int Promotions, IReadOnlyList<Booking> Recent);
public record AdminSchedulePage(Court Court, DateOnly Date, IReadOnlyList<Court> Courts, IReadOnlyList<Slot> Slots);
public class BookingRuleException(string message) : Exception(message);

public static class SportsAssets
{
    public static string Key(string sport) => sport.ToLowerInvariant() switch
    {
        var s when s.Contains("ฟุตบอล") || s.Contains("football") => "football",
        var s when s.Contains("เทนนิส") || s.Contains("tennis") => "tennis",
        var s when s.Contains("แบด") || s.Contains("badminton") => "badminton",
        _ => "other"
    };
    public static string Court(string sport) => $"/images/sports/{Key(sport)}.png";
    public static string Equipment(string name, string sport) => $"/images/equipment/{(name.Contains("รองเท้า") ? "shoes" : name.Contains("ลูก") ? Key(sport) == "badminton" ? "shuttlecock" : Key(sport) == "tennis" ? "tennis-ball" : "football" : name.Contains("เสื้อ") ? "bib" : Key(sport) == "tennis" ? "tennis-racket" : "badminton-racket")}.svg";
}
public class ReviewForm
{
    public long CourtId { get; set; }
    [Range(1,5)] public int Rating { get; set; }
    [StringLength(500)] public string? Comment { get; set; }
}
public record CourtReview(string MemberName, int Rating, string Comment, string CreatedAt);
public record CourtDetailsPage(Court Court, IReadOnlyList<CourtReview> Reviews, bool CanReview);
public record RevenueDay(DateOnly Date, int Bookings, long PaidSatang);
public record RevenuePage(DateOnly From, DateOnly To, long PaidSatang, long PendingSatang, int Cancelled, IReadOnlyList<RevenueDay> Days, IReadOnlyList<Booking> Bookings);
public class ProfileForm
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, EmailAddress, StringLength(150)] public string Email { get; set; } = "";
    public string? CurrentPassword { get; set; }
}
public class ForgotPasswordForm
{
    [Required, EmailAddress, StringLength(150)] public string Email { get; set; } = "";
}
public class ResetPasswordForm
{
    [Required] public string Token { get; set; } = "";
    [Required, StringLength(128, MinimumLength=8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password))] public string ConfirmPassword { get; set; } = "";
}

public class DamageReportForm
{
    public IFormFile? Evidence { get; set; }
    [StringLength(200)] public string? Subject { get; set; }
    [Required, RegularExpression("^(Court|Equipment|Other)$", ErrorMessage="กรุณาเลือกประเภทปัญหา")] public string Category { get; set; } = "Court";
    public string? EquipmentId { get; set; }
    [Required(ErrorMessage="กรุณาระบุรายละเอียด"), StringLength(2000,MinimumLength=5,ErrorMessage="ระบุรายละเอียด 5–2,000 ตัวอักษร")] public string Description { get; set; } = "";
}
public class DamageReportUpdate
{
    [Required] public string Status { get; set; } = "Reported";
    [StringLength(1000)] public string? AdminNote { get; set; }
}
public record DamageReport(long Id,long? BookingId,string Subject,DateOnly? PlayDate,string MemberName,string MemberEmail,
    string Category,string EquipmentName,string Description,string Status,string CreatedAt,string? UpdatedAt,string AdminNote,string UpdatedByName)
{
    public DamageClaim? Claim { get; init; }
    public IReadOnlyList<DamageEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<DamageClaimEvent> Events { get; init; } = [];
    public string DisplayStatus => Claim?.Status ?? Status;
    public static readonly string[] Statuses=["Reported","InProgress","Resolved"];
    public static string Label(string status)=>status switch{"Reported"=>"รอตรวจสอบ","InProgress"=>"กำลังดำเนินการ","Resolved"=>"แก้ไขแล้ว",_=>status};
    public string TargetLabel=>Category=="Court"?"สนาม · "+Subject:Category=="Equipment"?"อุปกรณ์ · "+EquipmentName:"ปัญหาทั่วไป · "+Subject;
    public string Reference=>$"DM{Id:000000}";
}
public record DamageReportPage(Booking? Booking,DamageReportForm Form,IReadOnlyList<DamageReport> Reports);
public record DamageReportListPage(IReadOnlyList<DamageReport> Reports,string? Status);
