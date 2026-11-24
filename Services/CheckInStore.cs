using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CourtBook.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeCheckIns(DbConnection c)
    {
        using var tx=BeginWrite(c);
        using var schema=Cmd(c,"""
            CREATE TABLE IF NOT EXISTS BookingEntryPasses (
              BookingId BIGINT PRIMARY KEY REFERENCES Bookings(Id),Token TEXT NOT NULL UNIQUE,CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS BookingCheckIns (
              BookingId BIGINT PRIMARY KEY REFERENCES Bookings(Id),CheckedInAt TEXT NOT NULL,CheckedInBy BIGINT NOT NULL REFERENCES Users(Id),
              Method TEXT NOT NULL CHECK(Method IN ('Qr','Code')));
            CREATE TABLE IF NOT EXISTS BookingCheckOuts (
              BookingId BIGINT PRIMARY KEY REFERENCES BookingCheckIns(BookingId),CheckedOutAt TEXT NOT NULL,
              CheckedOutBy BIGINT NOT NULL REFERENCES Users(Id));
            """,tx);schema.ExecuteNonQuery();
        if(c is Npgsql.NpgsqlConnection)
        {
            using var rls=Cmd(c,"ALTER TABLE BookingEntryPasses ENABLE ROW LEVEL SECURITY; ALTER TABLE BookingCheckIns ENABLE ROW LEVEL SECURITY; ALTER TABLE BookingCheckOuts ENABLE ROW LEVEL SECURITY",tx);rls.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public BookingEntryPass? EntryPass(long id,long userId)
    {
        var booking=Bookings(userId).SingleOrDefault(b=>b.Id==id);if(booking is null)return null;
        if(!booking.HasEntryPass)return new(booking,null);
        using var c=Open();using var tx=BeginWrite(c);
        using var insert=Cmd(c,"INSERT INTO BookingEntryPasses(BookingId,Token,CreatedAt) VALUES($id,$token,$now) ON CONFLICT(BookingId) DO NOTHING",tx,
            ("$id",id),("$token",WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))),("$now",DateTime.UtcNow.ToString("O")));insert.ExecuteNonQuery();
        using var find=Cmd(c,"SELECT Token FROM BookingEntryPasses WHERE BookingId=$id",tx,("$id",id));var token=(string)find.ExecuteScalar()!;
        tx.Commit();return new(booking,"COURTBOOK:"+token);
    }

    private static (long Id,string Method) ResolveEntryCode(DbConnection c,string? code,DbTransaction? tx=null)
    {
        code=(code??"").Trim();
        if(code.Length>128)throw new BookingRuleException("รหัสยาวเกินไป กรุณาใช้ QR หรือรหัสจองของ COURTBOOK");
        if(code.StartsWith("COURTBOOK:",StringComparison.Ordinal))
        {
            var token=code[10..];
            if(!Regex.IsMatch(token,@"\A[A-Za-z0-9_-]{43}\z"))throw new BookingRuleException("QR ไม่ถูกต้อง กรุณาให้ลูกค้าเปิด QR จากบัญชีของตนเอง");
            using var find=Cmd(c,"SELECT BookingId FROM BookingEntryPasses WHERE Token=$token",tx,("$token",token));
            var id=find.ExecuteScalar();if(id is null)throw new BookingRuleException("ไม่พบ QR นี้ในระบบ");
            return ((long)id,"Qr");
        }
        var number=code.StartsWith("CB",StringComparison.OrdinalIgnoreCase)?code[2..]:code;
        if(!Regex.IsMatch(number,@"\A[0-9]{1,19}\z")||!long.TryParse(number,out var bookingId)||bookingId<1)
            throw new BookingRuleException("กรอกรหัสจอง เช่น CB000001 หรือสแกน QR ของรายการจอง");
        return (bookingId,"Code");
    }

    public (Booking Booking,string Method) LookupEntry(string code)
    {
        using var c=Open();var entry=ResolveEntryCode(c,code);
        var booking=Bookings().SingleOrDefault(b=>b.Id==entry.Id)??throw new BookingRuleException("ไม่พบรายการจอง กรุณาตรวจรหัสอีกครั้ง");
        return (booking,entry.Method);
    }

    public long ConfirmBookingCheckIn(long adminId,string code,bool identityVerified)
    {
        if(!identityVerified)throw new BookingRuleException("กรุณาตรวจสอบชื่อผู้จองและรายละเอียดก่อนยืนยันเช็กอิน");
        using var c=Open();using var tx=BeginWrite(c);RequireAdmin(c,adminId,tx);
        var entry=ResolveEntryCode(c,code,tx);
        using var find=Cmd(c,"""
            SELECT b.PlayDate,b.StartHour,b.Hours,b.Status,b.PaidAt,b.NetSatang,ci.CheckedInAt
            FROM Bookings b LEFT JOIN BookingCheckIns ci ON ci.BookingId=b.Id WHERE b.Id=$id
            """,tx,("$id",entry.Id));using var r=find.ExecuteReader();
        if(!r.Read())throw new BookingRuleException("ไม่พบรายการจอง");
        var booking=new Booking{Id=entry.Id,Date=Date(r.GetString(0)),StartHour=r.GetInt32(1),Hours=r.GetInt32(2),ApprovalStatus=r.GetString(3),
            PaidAt=r.IsDBNull(4)?null:r.GetString(4),NetSatang=r.GetInt64(5),CheckIn=r.IsDBNull(6)?null:new(r.GetString(6),"","")};r.Close();
        var reason=booking.CheckInBlockReason(CourtTime.Now);if(reason is not null)throw new BookingRuleException(reason);
        using var insert=Cmd(c,"INSERT INTO BookingCheckIns(BookingId,CheckedInAt,CheckedInBy,Method) VALUES($id,$now,$admin,$method) ON CONFLICT(BookingId) DO NOTHING",tx,
            ("$id",entry.Id),("$now",DateTime.UtcNow.ToString("O")),("$admin",adminId),("$method",entry.Method));
        if(insert.ExecuteNonQuery()!=1)throw new BookingRuleException("รายการนี้เช็กอินแล้ว");
        tx.Commit();return entry.Id;
    }

    private static void EnsureNotCheckedIn(DbConnection c,DbTransaction tx,long id)
    {
        using var cmd=Cmd(c,"SELECT COUNT(*) FROM BookingCheckIns WHERE BookingId=$id",tx,("$id",id));
        if((long)cmd.ExecuteScalar()!>0)throw new BookingRuleException("รายการนี้เช็กอินเข้าใช้งานแล้ว ไม่สามารถยกเลิกหรือคืนเงินได้");
    }

    public void ConfirmBookingCheckOut(long id,long adminId,bool equipmentVerified)
    {
        if(!equipmentVerified)throw new BookingRuleException("กรุณาตรวจรับอุปกรณ์และยืนยันว่าลูกค้าออกจากสนามแล้ว");
        using var c=Open();using var tx=BeginWrite(c);RequireAdmin(c,adminId,tx);
        using var find=Cmd(c,"""
            SELECT b.Status,b.PaidAt,b.NetSatang,ci.BookingId,co.BookingId FROM Bookings b
            LEFT JOIN BookingCheckIns ci ON ci.BookingId=b.Id LEFT JOIN BookingCheckOuts co ON co.BookingId=b.Id WHERE b.Id=$id
            """,tx,("$id",id));using var r=find.ExecuteReader();
        if(!r.Read())throw new BookingRuleException("ไม่พบรายการจอง");
        if(!r.IsDBNull(4))throw new BookingRuleException("รายการนี้เช็กเอาต์แล้ว");
        if(r.IsDBNull(3)||r.GetString(0)!="Confirmed"||(r.IsDBNull(1)&&r.GetInt64(2)>0))
            throw new BookingRuleException("เช็กเอาต์ได้เฉพาะรายการที่เช็กอินและชำระเงินครบแล้ว");
        r.Close();
        using var outstanding=Cmd(c,"SELECT COALESCE(SUM(IssuedQuantity-GoodQuantity-DamagedQuantity),0) FROM EquipmentLoans WHERE BookingId=$id",tx,("$id",id));
        if(Convert.ToInt64(outstanding.ExecuteScalar())>0)throw new BookingRuleException("ยังมีอุปกรณ์ที่ลูกค้าไม่คืน กรุณารับคืนให้ครบก่อนเช็กเอาต์");
        using var insert=Cmd(c,"INSERT INTO BookingCheckOuts(BookingId,CheckedOutAt,CheckedOutBy) VALUES($id,$now,$admin)",tx,
            ("$id",id),("$now",DateTime.UtcNow.ToString("O")),("$admin",adminId));insert.ExecuteNonQuery();
        tx.Commit();
    }
}
