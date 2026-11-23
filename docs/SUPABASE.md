# ใช้ฐานข้อมูล Supabase กับ COURTBOOK

โปรเจกต์นี้เชื่อม PostgreSQL ของ Supabase จาก ASP.NET Core ผ่าน Npgsql โดยตรง ระบบบัญชีสมาชิก รหัสผ่าน การจอง กระเป๋าเงิน และรูปภาพยังใช้โครงสร้างของ COURTBOOK จึงไม่ต้องใช้ Supabase Auth หรือ API Key ในหน้าเว็บ

## 1. สร้างโปรเจกต์ Supabase

1. เข้า https://supabase.com/dashboard และสร้าง New project
2. ตั้ง Database password และเก็บไว้ในเครื่อง
3. เปิด **Connect → Session pooler → View parameters** แล้วจด Host, Port, Database และ User ตามที่แสดงจริงในโปรเจกต์ โดย Session pooler ใช้พอร์ต 5432 และรองรับ IPv4

อ้างอิง: [คู่มือการเชื่อม PostgreSQL ของ Supabase](https://supabase.com/docs/guides/database/connecting-to-postgres)

## 2. เลือกฐานข้อมูลใหม่หรือย้ายข้อมูลเดิม

**เริ่มใหม่:** เปิด SQL Editor ใน Supabase แล้วรันไฟล์ [Database/supabase.sql](../Database/supabase.sql) ทั้งไฟล์ ตารางจะสร้างอัตโนมัติเมื่อเปิดแอปด้วยเช่นกัน

**ย้ายข้อมูล SQLite เดิม:** หยุดแอปก่อน และอย่าเปิดให้ผู้ใช้เพิ่มข้อมูลจนย้ายและตรวจสอบเสร็จ สำรองไฟล์ `App_Data/courtbook.db` ไว้ จากนั้นรันในโฟลเดอร์โปรเจกต์:

```powershell
python Database/export_sqlite.py App_Data/courtbook.db courtbook.migration.sql
```

นำไฟล์ `courtbook.migration.sql` ไปใช้กับฐานข้อมูล Supabase ที่ยังไม่มีตาราง COURTBOOK หรือมีตารางเปล่าเท่านั้น **ห้ามรัน supabase.sql หรือเปิดแอปก่อนนำเข้าข้อมูล** เพราะจะสร้างข้อมูลเริ่มต้น ทำให้เครื่องมือนำเข้าปฏิเสธฐานข้อมูลที่ไม่ว่าง หากเริ่มไปแล้ว ให้ใช้โปรเจกต์ทดสอบใหม่ ห้ามล้างข้อมูลที่ใช้งานอยู่เพื่อแก้ปัญหา

ไฟล์ส่งออกมีข้อมูลสมาชิก password hash รูปภาพ และหลักฐานโอนเงิน ให้เก็บเป็นส่วนตัวและไม่ commit เข้า Git เครื่องมืออ่าน SQLite แบบ read-only ใช้ snapshot ตรวจ foreign key และนำเข้าภายใน transaction เดียว พร้อมตรวจจำนวนแถวและตั้งลำดับ ID ใหม่ หากใช้ psql ให้รัน `psql -v ON_ERROR_STOP=1 --file courtbook.migration.sql` โดยตั้งค่าการเชื่อมต่อในเครื่อง ถ้าไฟล์ใหญ่เกิน SQL Editor ให้ใช้ psql

## 3. ตั้งค่าการเชื่อมต่อในเครื่อง

เปิด PowerShell ในโฟลเดอร์โปรเจกต์ ติดตั้ง .NET 10 SDK แล้วรัน:

```powershell
dotnet restore
dotnet user-secrets set "Database:Provider" "Supabase"
dotnet user-secrets set "ConnectionStrings:Supabase" "Host=YOUR_POOLER_HOST;Port=5432;Database=postgres;Username=postgres.YOUR_PROJECT_REF;Password=YOUR_DATABASE_PASSWORD;SSL Mode=Require;Maximum Pool Size=10;Timeout=15"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project CourtBook.csproj
```

แทน Host และ Username ด้วยค่าจาก Connect ของโปรเจกต์ อย่าเดาชื่อ Host จาก region ตัวอย่างนี้เป็นรูปแบบ Npgsql `key=value;` ไม่ใช่ URL `postgresql://` หากรหัสผ่านมี `;` หรือเครื่องหมายคำพูด ให้ escape ตามรูปแบบ connection string ของ Npgsql

User Secrets เก็บค่าไว้นอกโฟลเดอร์โปรเจกต์ ใช้เฉพาะการพัฒนา ห้ามใส่รหัสผ่านจริงใน `appsettings.json` หรือส่งในแชต สำหรับเซิร์ฟเวอร์ให้ใช้ environment variables `Database__Provider=Supabase` และ `ConnectionStrings__Supabase=...` และพิจารณา `SSL Mode=VerifyFull` พร้อม Root Certificate ตามการตั้งค่า Supabase ของคุณ

ระบบสร้างตารางเมื่อเริ่มแอป บัญชีเชื่อมต่อต้องมีสิทธิ์สร้าง/แก้ไขตาราง โดยตัวอย่างนี้ใช้ผู้ใช้ postgres จาก Connect ตารางเปิด RLS โดยไม่มี policy ให้ anon/authenticated จึงเข้าถึงผ่านเซิร์ฟเวอร์ COURTBOOK ที่เชื่อมด้วยสิทธิ์เจ้าของตารางเท่านั้น

ค่าปัจจุบัน `Demo:SeedAccounts=true` จะสร้างบัญชีตัวอย่างเมื่อ Users ว่าง ใช้สำหรับงานทดสอบเท่านั้น ก่อนใช้งานจริงให้ตั้ง `Demo__SeedAccounts=false` และเตรียมบัญชีผู้ดูแลของคุณ การปิดค่านี้ไม่ลบบัญชีตัวอย่างที่สร้างแล้ว

## 4. ตรวจสอบหลังเชื่อมต่อ

1. รัน `dotnet build` แล้วเปิดเว็บจาก `run.cmd` หรือคำสั่งด้านบน
2. ตรวจ Table Editor ว่ามี users, courts, bookings, topups, walletentries, demopayments และตารางอื่นครบ 12 ตาราง ชื่อใน PostgreSQL เป็นตัวพิมพ์เล็ก
3. ทดลองสมัครสมาชิก เข้าสู่ระบบ จองสนาม อัปโหลดรูป/สลิป และตรวจจากผู้ดูแล
4. ทดสอบเติมเงิน อนุมัติเงิน ซื้อแพ็ก และชำระค่าจอง ตรวจว่ายอดเงินกับประวัติตรงกัน
5. ปิดและเปิดแอปใหม่ ข้อมูลต้องยังอยู่ หากย้ายข้อมูล ให้เทียบจำนวนแถวกับผลจากเครื่องมือส่งออก รวมถึงยอดเงิน รายการจอง และรูปภาพ ก่อนเปิดให้ผู้ใช้ใช้งาน

ธุรกรรมที่เขียนหลายขั้นตอนใช้ PostgreSQL advisory lock ร่วมกัน เพื่อรักษาการตรวจยอดเงินและช่วงเวลาจองแบบเดียวกับ SQLite เหมาะกับโปรเจกต์ขนาดนี้ หากปริมาณงานเพิ่มขึ้นจึงปรับเป็น lock รายสมาชิก/สนาม

## กลับไปใช้ SQLite

ก่อนเปิดใช้งานจริง สามารถกลับไปฐานข้อมูลเดิมได้ด้วย:

```powershell
dotnet user-secrets set "Database:Provider" "Sqlite"
```

หากใช้ environment variable ให้แก้ `Database__Provider` ด้วย เพราะมีลำดับความสำคัญสูงกว่า User Secrets ไฟล์ SQLite เดิมยังอยู่ตามเดิม ข้อมูลที่เพิ่มใหม่ใน Supabase จะไม่ซิงก์กลับ SQLite อัตโนมัติ จึงต้องวางแผนส่งข้อมูลกลับก่อนสลับฐานข้อมูลหลังเปิดใช้งานจริง

## Verification status

Automatic demo top-ups are enabled by `Demo:AutomaticTopUps=true`. The wallet opens a simulated checkout with success, failure, and cancellation outcomes; no external payment provider or real charge is involved. Only a successful, unexpired, member-owned pending payment credits the wallet. Duplicate or concurrent submissions cannot credit twice. Demo credits are labelled in the wallet ledger and share the same balance used for bookings and membership plans. Set `Demo__AutomaticTopUps=false` to hide and disable all demo payment endpoints.

The latest wallet HTTP tests cover the demo flow, ownership, CSRF, invalid amounts, concurrent retries, failure/cancellation, expiry, atomic rollback, disabled configuration, legacy wallet preservation and restart persistence on disposable SQLite databases. Direct Supabase verification for this update was blocked by the tool environment's outbound socket restrictions; no remote test schema was created.

- `dotnet build CourtBook.csproj -o artifacts/supabase-check`: passed, zero warnings and errors.
- `tests/smoke.py` and `tests/wallet_smoke.py`: passed against disposable SQLite databases with the new build.
- `python tests/export_smoke.py`: passed. Existing SQLite data also exported read-only with matching row counts.
- Supabase connection still requires local database credentials. PostgreSQL runtime checks remain pending until startup succeeds.

For interactive setup on Windows, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\connect-supabase.ps1`. Copy the User value from Session pooler and enter your database password at the hidden prompt. The script saves the connection in User Secrets outside the project and starts the verified build. It creates cloud tables without importing the existing SQLite records. For data migration, follow the import steps above before starting the cloud app.
