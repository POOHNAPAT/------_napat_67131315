using System.Data.Common;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeEquipment(DbConnection c)
    {
        using var tx = BeginWrite(c);
        using var schema = Cmd(c, """
            CREATE TABLE IF NOT EXISTS RentalEquipment (
                Id TEXT PRIMARY KEY, SportTypeId BIGINT NOT NULL REFERENCES SportTypes(Id),
                Name TEXT NOT NULL, Unit TEXT NOT NULL, PriceSatang BIGINT NOT NULL CHECK(PriceSatang > 0),
                IsActive INTEGER NOT NULL DEFAULT 1);
            INSERT INTO RentalEquipment(Id,SportTypeId,Name,Unit,PriceSatang,IsActive)
                SELECT 'racket',1,'ไม้แบดมินตัน','อัน',5000,1 WHERE EXISTS(SELECT 1 FROM SportTypes WHERE Id=1)
                ON CONFLICT(Id) DO NOTHING;
            INSERT INTO RentalEquipment(Id,SportTypeId,Name,Unit,PriceSatang,IsActive)
                SELECT 'shoes',1,'รองเท้ากีฬา','คู่',3000,1 WHERE EXISTS(SELECT 1 FROM SportTypes WHERE Id=1)
                ON CONFLICT(Id) DO NOTHING;
            """, tx);
        schema.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<RentalEquipment> Equipment(long? sportTypeId = null, bool includeInactive = false)
    {
        using var c = Open();
        return GetEquipment(c, sportTypeId, includeInactive);
    }

    private static IReadOnlyList<RentalEquipment> GetEquipment(DbConnection c, long? sportTypeId, bool includeInactive = false, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, """
            SELECT e.Id,e.Name,e.Unit,e.PriceSatang,e.SportTypeId,s.Name,e.IsActive,e.ImageUrl
            FROM RentalEquipment e JOIN SportTypes s ON s.Id=e.SportTypeId
            WHERE ($sport IS NULL OR e.SportTypeId=$sport) AND ($all=1 OR e.IsActive=1)
            ORDER BY s.Id,e.Name,e.Id
            """, tx, ("$sport", sportTypeId), ("$all", includeInactive));
        using var r = cmd.ExecuteReader();
        var items = new List<RentalEquipment>();
        while (r.Read()) items.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetInt32(6)==1,r.GetString(7)));
        return items;
    }

    private static IReadOnlyList<BookingEquipment> SelectEquipment(DbConnection c, long sportTypeId, Dictionary<string,int>? quantities, DbTransaction? tx)
    {
        if (quantities is null || quantities.Count == 0) return [];
        var items = GetEquipment(c, sportTypeId, tx: tx);
        if (quantities.Any(q => q.Value < 0 || q.Value > 10 || (q.Value > 0 && !items.Any(e => e.Id == q.Key))))
            throw new BookingRuleException("เลือกอุปกรณ์ที่เปิดให้เช่าสำหรับกีฬานี้เท่านั้น จำนวน 0–10 ต่อรายการ");
        return items.Where(e => quantities.GetValueOrDefault(e.Id) > 0)
            .Select(e => new BookingEquipment(e.Id,e.Name,e.Unit,e.PriceSatang,quantities[e.Id])).ToArray();
    }

    public bool SaveEquipment(EquipmentForm form)
    {
        if (string.IsNullOrWhiteSpace(form.Name) || form.Name.Trim().Length > 100
            || string.IsNullOrWhiteSpace(form.Unit) || form.Unit.Trim().Length > 30
            || form.Price < 0.01m || form.Price > 100000m)
            throw new BookingRuleException("กรุณาระบุชื่อ หน่วยนับ และราคาอุปกรณ์ให้ถูกต้อง");
        if (!string.IsNullOrWhiteSpace(form.ImageUrl) && !(form.ImageUrl.StartsWith("/images/",StringComparison.Ordinal) && !form.ImageUrl.StartsWith("//",StringComparison.Ordinal))
            && !(Uri.TryCreate(form.ImageUrl,UriKind.Absolute,out var imageUri) && imageUri.Scheme == "https"))
            throw new BookingRuleException("รูปอุปกรณ์ต้องเป็น HTTPS หรือไฟล์ใน /images/");
        using var c = Open();
        using var tx = BeginWrite(c);
        using var sport = Cmd(c,"SELECT COUNT(*) FROM SportTypes WHERE Id=$sport",tx,("$sport",form.SportTypeId));
        if ((long)sport.ExecuteScalar()! == 0) throw new BookingRuleException("ไม่พบประเภทกีฬาที่เลือก");
        var isNew = string.IsNullOrEmpty(form.Id);
        using var cmd = Cmd(c, isNew ? """
            INSERT INTO RentalEquipment(Id,SportTypeId,Name,Unit,PriceSatang,IsActive,ImageUrl)
            VALUES($id,$sport,$name,$unit,$price,$active,$imageUrl)
            """ : """
            UPDATE RentalEquipment SET ImageUrl=$imageUrl,SportTypeId=$sport,Name=$name,Unit=$unit,PriceSatang=$price,IsActive=$active WHERE Id=$id
            """,tx,("$id",isNew ? Guid.NewGuid().ToString("N") : form.Id),("$sport",form.SportTypeId),
            ("$name",form.Name.Trim()),("$unit",form.Unit.Trim()),("$imageUrl",form.ImageUrl?.Trim()??""),("$price",Satang(form.Price)),("$active",form.IsActive));
        var saved = cmd.ExecuteNonQuery() == 1;
        tx.Commit();
        return saved;
    }
}
