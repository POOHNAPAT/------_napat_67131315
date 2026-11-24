using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class CheckInLookupForm
{
    [Required(ErrorMessage="กรุณาสแกน QR หรือกรอกรหัสจอง"),StringLength(128)] public string Code { get; set; } = "";
}
public class CheckInConfirmationForm:CheckInLookupForm
{
    public bool IdentityVerified { get; set; }
}
public record BookingCheckIn(string CheckedInAt,string StaffName,string Method);
public record BookingCheckOut(string CheckedOutAt,string StaffName);
public class CheckOutConfirmationForm
{
    public bool EquipmentVerified { get; set; }
}
public record BookingEntryPass(Booking Booking,string? Payload);
public record CheckInPage(Booking? Booking,string Code,string? Method,IReadOnlyList<Booking> Today);
