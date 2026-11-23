using System.ComponentModel.DataAnnotations;

namespace CourtBook.Models;

public class EquipmentStockForm
{
    [Range(0,1000000)] public int TotalQuantity { get; set; }
    [Range(0,1000000)] public int RepairQuantity { get; set; }
    [Range(0,int.MaxValue)] public int Revision { get; set; }
    [Required,StringLength(1000,MinimumLength=5)] public string Note { get; set; } = "";
}
public class EquipmentHandoverForm
{
    [Required] public string EquipmentId { get; set; } = "";
    [Range(0,int.MaxValue)] public int Revision { get; set; }
    [Range(0,10)] public int Quantity { get; set; }
    [Range(0,10)] public int GoodQuantity { get; set; }
    [Range(0,10)] public int DamagedQuantity { get; set; }
    [Required,StringLength(1000,MinimumLength=5)] public string Note { get; set; } = "";
    public IFormFile? Image { get; set; }
}
public record EquipmentLoan(long BookingId,string EquipmentId,string Name,string Unit,int ReservedQuantity,int IssuedQuantity,int GoodQuantity,int DamagedQuantity,int Revision,
    DateOnly Date,int StartHour,int Hours,string BookingStatus,string MemberName,string? PaidAt)
{
    public int Outstanding=>IssuedQuantity-GoodQuantity-DamagedQuantity;
    public int Unissued=>ReservedQuantity-IssuedQuantity;
    public int RemainingReservation=>ReservedQuantity-GoodQuantity-DamagedQuantity;
    public DateTime Starts=>Date.ToDateTime(new TimeOnly(StartHour,0));
    public DateTime Ends=>Starts.AddHours(Hours);
    public bool CanIssue=>BookingStatus=="Confirmed"&&PaidAt is not null&&CourtTime.Now>=Starts&&CourtTime.Now<Ends&&Unissued>0;
    public bool Overdue=>Outstanding>0&&Ends<=CourtTime.Now;
    public string StatusLabel=>Overdue?"เกินกำหนดคืน":Outstanding>0?"อยู่กับลูกค้า":BookingStatus=="Cancelled"?"ยกเลิกการจอง":IssuedQuantity>0&&IssuedQuantity==GoodQuantity+DamagedQuantity&&Unissued==0?"คืนครบแล้ว":IssuedQuantity>0?"คืนแล้วบางส่วน / ยังส่งมอบไม่ครบ":"รอส่งมอบ";
}
public record EquipmentInventory(RentalEquipment Equipment,int TotalQuantity,int RepairQuantity,int Revision,int IssuedQuantity,int OverdueQuantity,int PeakReserved)
{
    public int RentableQuantity=>TotalQuantity-RepairQuantity;
    public int InStoreQuantity=>Math.Max(0,RentableQuantity-IssuedQuantity);
    public int ShortageQuantity=>Math.Max(0,PeakReserved-RentableQuantity);
}
public record EquipmentMovement(long Id,long? BookingId,string EquipmentId,string EquipmentName,string Kind,int Quantity,int GoodQuantity,int DamagedQuantity,
    int TotalDelta,int RepairDelta,string Note,string ActorName,string CreatedAt,bool HasImage,long? DamageReportId)
{
    public string KindLabel=>Kind switch{"Issue"=>"ส่งมอบ","Return"=>"รับคืน","Opening"=>"ตั้งสต็อกเริ่มต้น",_=>"ปรับสต็อก / ซ่อมเสร็จ"};
}
public record EquipmentInventoryPage(IReadOnlyList<EquipmentInventory> Items,IReadOnlyList<EquipmentMovement> Movements,IReadOnlyList<EquipmentLoan> Outstanding);
public record EquipmentStockPage(EquipmentInventory Item,IReadOnlyList<EquipmentMovement> Movements);
public record EquipmentHandoverPage(Booking Booking,IReadOnlyList<EquipmentLoan> Loans,IReadOnlyList<EquipmentMovement> Movements);
public record EquipmentAvailability(string Id,int Available);
