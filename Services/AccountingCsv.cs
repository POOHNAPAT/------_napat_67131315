using System.Globalization;
using System.Text;
using CourtBook.Models;

namespace CourtBook.Services;

public static class AccountingCsv
{
    public static byte[] Export(AccountingPage page,bool admin)
    {
        var rows=new List<object?[]>{new object?[]{"วันที่","ประเภท","เลขอ้างอิง","รายละเอียด","จำนวนเงิน (บาท)","สถานะ","ช่องทาง","ใบเสร็จ"}};
        if(admin)rows[0]=[..rows[0],"ผู้เกี่ยวข้อง"];
        foreach(var e in page.Entries)
        {
            object?[] row=[CourtTime.Iso(e.Date),e.KindLabel,e.Reference,e.Description,e.AmountSatang/100m,e.Status,e.Method,e.ReceiptId??""];
            rows.Add(admin?[..row,e.MemberName]:row);
        }
        static string Cell(object? value)
        {
            if(value is decimal or long or int)return Convert.ToString(value,CultureInfo.InvariantCulture)!;
            var text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
            var check=text.TrimStart();
            if(check.Length>0&&"=+-@".Contains(check[0]))text="'"+text;
            return "\""+text.Replace("\"","\"\"")+"\"";
        }
        var csv=string.Join("\r\n",rows.Select(row=>string.Join(",",row.Select(Cell))))+"\r\n";
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    }
}
