using System.Data.Common;
using System.Text.Json;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeReceipts(DbConnection c)
    {
        using var tx=BeginWrite(c);
        using var schema=Cmd(c,"""
            CREATE TABLE IF NOT EXISTS PaymentReceipts (
              Id TEXT PRIMARY KEY,UserId BIGINT NOT NULL REFERENCES Users(Id),
              BookingId BIGINT UNIQUE REFERENCES Bookings(Id),MembershipId BIGINT UNIQUE REFERENCES Memberships(Id),
              SnapshotJson TEXT NOT NULL,CreatedAt TEXT NOT NULL,
              CHECK((BookingId IS NOT NULL AND MembershipId IS NULL) OR (BookingId IS NULL AND MembershipId IS NOT NULL)));
            CREATE INDEX IF NOT EXISTS IX_PaymentReceipts_User ON PaymentReceipts(UserId);
            """,tx);schema.ExecuteNonQuery();
        // Backfill historical settled transactions once. Later edits never change the saved receipt.
        List<long> Ids(string sql)
        {
            using var cmd=Cmd(c,sql,tx);using var r=cmd.ExecuteReader();var ids=new List<long>();
            while(r.Read())ids.Add(r.GetInt64(0));return ids;
        }
        foreach(var id in Ids("SELECT b.Id FROM Bookings b WHERE b.PaidAt IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PaymentReceipts p WHERE p.BookingId=b.Id)"))IssueBookingReceipt(c,tx,id);
        foreach(var id in Ids("SELECT m.Id FROM Memberships m WHERE EXISTS(SELECT 1 FROM WalletEntries w WHERE w.MembershipId=m.Id AND w.AmountSatang=-m.PriceSatang) AND NOT EXISTS(SELECT 1 FROM PaymentReceipts p WHERE p.MembershipId=m.Id)"))IssueMembershipReceipt(c,tx,id);
        tx.Commit();
    }

    private static void SaveReceipt(DbConnection c,DbTransaction tx,long userId,long? bookingId,long? membershipId,ReceiptSnapshot snapshot)
    {
        var id=bookingId.HasValue?$"RC-BK-{bookingId:000000}":$"RC-MB-{membershipId:000000}";
        using var cmd=Cmd(c,"""
            INSERT INTO PaymentReceipts(Id,UserId,BookingId,MembershipId,SnapshotJson,CreatedAt)
            VALUES($id,$user,$booking,$membership,$snapshot,$now) ON CONFLICT DO NOTHING
            """,tx,("$id",id),("$user",userId),("$booking",bookingId),("$membership",membershipId),
            ("$snapshot",JsonSerializer.Serialize(snapshot)),("$now",DateTime.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    private static void IssueBookingReceipt(DbConnection c,DbTransaction tx,long id)
    {
        using var cmd=Cmd(c,"""
            SELECT b.UserId,u.Name,u.Email,b.CourtName,b.SportTypeName,b.PlayDate,b.StartHour,b.Hours,b.PriceSatang,
              b.EquipmentJson,b.GrossSatang,b.DiscountSatang,b.NetSatang,b.PromotionName,b.PaidAt,b.PaymentReference,COALESCE((SELECT Hours FROM MembershipQuotaUses WHERE BookingId=b.Id),0)
            FROM Bookings b JOIN Users u ON u.Id=b.UserId WHERE b.Id=$id AND b.PaidAt IS NOT NULL
            """,tx,("$id",id));using var r=cmd.ExecuteReader();
        if(!r.Read())throw new BookingRuleException("ออกใบเสร็จได้เมื่อยืนยันรับชำระแล้วเท่านั้น");
        var user=r.GetInt64(0);var lines=new List<ReceiptLine>{new("ค่าเช่า "+r.GetString(3),r.GetInt32(7),"ชั่วโมง",r.GetInt64(8))};
        var equipment=JsonSerializer.Deserialize<List<BookingEquipment>>(r.GetString(9))??[];
        lines.AddRange(equipment.Select(e=>new ReceiptLine(e.Name,e.Quantity,e.Unit,e.PriceSatang)));
        var snapshot=new ReceiptSnapshot("Booking",r.GetString(1),r.GetString(2),$"CB{id:000000}",r.GetString(14),r.GetString(15),lines,
            r.GetInt64(10),r.GetInt64(11),r.GetInt64(12),r.GetString(13),r.GetString(3),r.GetString(4),Date(r.GetString(5)),
            $"{CourtTime.Hour(r.GetInt32(6))}–{CourtTime.Hour(r.GetInt32(6)+r.GetInt32(7))}",IncludedHours:r.GetInt32(16));
        r.Close();SaveReceipt(c,tx,user,id,null,snapshot);
    }

    private static void IssueMembershipReceipt(DbConnection c,DbTransaction tx,long id)
    {
        using var cmd=Cmd(c,"""
            SELECT m.UserId,u.Name,u.Email,m.PlanName,m.PriceSatang,m.DiscountPercent,m.Starts,m.Ends,m.CreatedAt,m.MonthlyHours,m.QuotaSportName
            FROM Memberships m JOIN Users u ON u.Id=m.UserId
            WHERE m.Id=$id AND EXISTS(SELECT 1 FROM WalletEntries w WHERE w.MembershipId=m.Id AND w.AmountSatang=-m.PriceSatang)
            """,tx,("$id",id));using var r=cmd.ExecuteReader();
        if(!r.Read())throw new BookingRuleException("ไม่พบรายการซื้อแพ็กที่ชำระสำเร็จ");
        var user=r.GetInt64(0);var price=r.GetInt64(4);
        var snapshot=new ReceiptSnapshot("Membership",r.GetString(1),r.GetString(2),$"MB{id:000000}",r.GetString(8),"กระเป๋าเงิน COURTBOOK",
            [new(r.GetString(3),1,"แพ็ก",price)],price,0,price,"",PlanName:r.GetString(3),Starts:Date(r.GetString(6)),Ends:Date(r.GetString(7)),DiscountPercent:r.GetInt32(5),MonthlyHours:r.GetInt32(9),QuotaSportName:r.GetString(10));
        r.Close();SaveReceipt(c,tx,user,null,id,snapshot);
    }

    public IReadOnlyList<PaymentReceipt> Receipts(long userId)
    {
        using var c=Open();using var cmd=Cmd(c,"SELECT Id,BookingId,MembershipId,SnapshotJson,CreatedAt FROM PaymentReceipts WHERE UserId=$user ORDER BY CreatedAt DESC,Id DESC",null,("$user",userId));
        using var r=cmd.ExecuteReader();var rows=new List<PaymentReceipt>();
        while(r.Read())rows.Add(new(r.GetString(0),r.IsDBNull(1)?null:r.GetInt64(1),r.IsDBNull(2)?null:r.GetInt64(2),JsonSerializer.Deserialize<ReceiptSnapshot>(r.GetString(3))!,r.GetString(4)));
        return rows;
    }

    public PaymentReceipt? FindReceipt(string id,long? userId)
    {
        using var c=Open();using var cmd=Cmd(c,"""
            SELECT p.Id,p.BookingId,p.MembershipId,p.SnapshotJson,p.CreatedAt
            FROM PaymentReceipts p WHERE p.Id=$id AND ($user IS NULL OR p.UserId=$user)
            AND ((p.BookingId IS NOT NULL AND EXISTS(SELECT 1 FROM Bookings b WHERE b.Id=p.BookingId AND b.PaidAt IS NOT NULL))
              OR (p.MembershipId IS NOT NULL AND EXISTS(SELECT 1 FROM Memberships m JOIN WalletEntries w ON w.MembershipId=m.Id WHERE m.Id=p.MembershipId AND w.AmountSatang=-m.PriceSatang)))
            """,null,("$id",id),("$user",userId));using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        var receipt=new PaymentReceipt(r.GetString(0),r.IsDBNull(1)?null:r.GetInt64(1),r.IsDBNull(2)?null:r.GetInt64(2),JsonSerializer.Deserialize<ReceiptSnapshot>(r.GetString(3))!,r.GetString(4));r.Close();
        return receipt with { Booking=receipt.BookingId.HasValue?Bookings(userId).SingleOrDefault(b=>b.Id==receipt.BookingId):null, Refund=receipt.BookingId.HasValue?Refunds(userId).SingleOrDefault(r=>r.BookingId==receipt.BookingId):null };
    }
}
