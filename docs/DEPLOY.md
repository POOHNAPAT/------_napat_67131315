# Deploy COURTBOOK ด้วย Docker และ Supabase

โปรเจกต์มี `Dockerfile`, `render.yaml` และ production configuration สำหรับรันเป็นสองบริการ: เว็บลูกค้าและเว็บผู้ดูแล แต่ละบริการมี URL ของตัวเองและใช้ Supabase เดียวกัน ไม่ใช้พอร์ต localhost หลายพอร์ตบนเว็บสาธารณะ

## ไฟล์ที่เตรียมแล้ว

- `Dockerfile`: build/publish .NET 10 แล้วรันด้วยผู้ใช้ app ที่ไม่ใช่ root
- `deploy/entrypoint.sh`: ฟัง `0.0.0.0:$PORT`, เลือกพอร์ทัลจาก `COURTBOOK_PORTAL=Customer` หรือ `Admin`
- `deploy/appsettings.Production.json`: เลือก Supabase ปิดบัญชีตัวอย่างและเครดิตทดลอง ใช้เฉพาะใน Docker image ไม่เปลี่ยนการตั้งค่า localhost เดิม
- `render.yaml`: สอง Docker Web Services และ health check `/healthz` ตั้ง `plan: free` โดยไม่มี disk แบบเสียเงิน

## ก่อนเผยแพร่

1. เลือกบัญชี Render และเชื่อม repository ของโปรเจกต์ ใช้ private repository ได้ โฟลเดอร์รากของ repository ต้องมี `CourtBook.csproj`, `Dockerfile` และ `render.yaml`
2. เตรียม Supabase connection string ของ Session pooler จากหน้า Connect ห้ามใส่รหัสผ่านใน Git, Dockerfile, ZIP หรือแชต ใช้ช่อง secret environment ของโฮสต์
3. ถ้าต้องใช้ข้อมูลเดิมใน SQLite ให้ทำตาม `SUPABASE.md` และย้ายข้อมูลก่อนเปิดแอปกับฐานปลายทาง ห้ามเปิดแอปสร้างข้อมูลเริ่มต้นในฐานเป้าหมายก่อน import ข้อมูลเดิม
4. เตรียมอีเมลและรหัสผ่านผู้ดูแลที่เดาได้ยาก 12–128 ตัวอักษร Bootstrap ใช้เฉพาะเมื่อฐานยังไม่มีผู้ดูแล ไม่เปลี่ยนรหัสผ่านหรือยกระดับบัญชีที่มีอยู่

## Deploy บน Render

1. สร้าง Blueprint จาก repository และใช้ `render.yaml`
2. กรอก secret ต่อไปนี้ให้ทั้งบริการลูกค้าและบริการผู้ดูแล:
   - `ConnectionStrings__Supabase`: Npgsql connection string เช่น `Host=YOUR_POOLER_HOST;Port=5432;Database=postgres;Username=postgres.YOUR_REF;Password=YOUR_PASSWORD;SSL Mode=Require;Maximum Pool Size=10;Timeout=15`
   - `Bootstrap__AdminEmail`: อีเมลผู้ดูแลใหม่ หากมีผู้ดูแลแล้วใช้บัญชีเดิมหลัง deploy
   - `Bootstrap__AdminPassword`: รหัสผ่านสำหรับสร้างผู้ดูแลครั้งแรก
3. ตรวจว่า runtime เป็น Docker และ health check เป็น `/healthz` โฮสต์จะกำหนด `PORT` ให้ ไม่ใช้ `dotnet watch` เป็นคำสั่ง production
4. เมื่อทั้งสองบริการ Live ให้เปิด URL ลูกค้าเพื่อสมัคร/จอง และเปิด URL ผู้ดูแลเพื่อเข้าหลังบ้าน ค่า URL จริงดูจาก Dashboard ไม่สมมติชื่อ domain ล่วงหน้า
5. หลังสร้างผู้ดูแลครั้งแรกแล้วสามารถลบ Bootstrap secrets ออกจากโฮสต์ได้ บัญชียังอยู่ใน Supabase

Render ตั้ง `RENDER_EXTERNAL_HOSTNAME` และ `RENDER_EXTERNAL_URL` ให้ entrypoint ใช้จำกัด AllowedHosts และกำหนดลิงก์กู้รหัสผ่านของพอร์ทัลนั้น สำหรับ custom domain ให้ตั้ง `AllowedHosts`, `Account__PublicOrigin` หรือ `Account__AdminOrigin` เป็นโดเมน HTTPS จริง

`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` ใน Blueprint ใช้เมื่อแอปอยู่หลัง reverse proxy ที่เชื่อถือได้ของ Render เพื่อรับ scheme/IP จาก proxy หากนำ Docker ไปเปิดกับอินเทอร์เน็ตโดยตรง ให้ปิดตัวแปรนี้และตั้ง trusted proxy/TLS ให้เหมาะสม

## ข้อมูลและ session

ธุรกรรม รูปสนาม สลิป รายงาน และใบเสร็จเก็บใน Supabase ไม่พึ่ง filesystem ของ container แต่ Data Protection keys อยู่ที่ `/app/keys` การ deploy บน filesystem ชั่วคราวทำให้ผู้ใช้ต้องเข้าสู่ระบบใหม่ และลิงก์กู้รหัสผ่านเดิมอาจหมดผลหลังเปลี่ยน container

หากต้องการรักษา session และ reset links ข้ามการ deploy ให้ mount persistent volume ที่ `/app/keys` หรือกำหนด `DataProtection__KeyDirectory` ไปยัง persistent mount ของแต่ละบริการ ตรวจแผนและค่าบริการก่อนเพิ่ม disk ไม่มีการสร้าง disk แบบเสียเงินในไฟล์นี้

SMTP ตั้งค่าเพิ่มเติมผ่าน `Email__Host`, `Email__From`, `Email__Username`, `Email__Password`, `Email__Port` ตาม README หากไม่ตั้งค่า การกู้รหัสผ่านจะแจ้งว่าระบบอีเมลยังไม่พร้อม ไม่มีการจำลองว่ามีการส่งอีเมลจริง

## ตรวจสอบหลัง deploy

- ลูกค้า `/healthz` และผู้ดูแล `/healthz` ตอบ `ok`
- เว็บลูกค้าเปิด `/Admin` ต้องไม่เข้าหลังบ้าน
- เว็บผู้ดูแลไม่เปิดสมัครบัญชีลูกค้า และเข้าสู่ระบบได้เฉพาะผู้ดูแล
- สมัครลูกค้า จอง อุปกรณ์ ชำระจริงตามขั้นตอน ออกใบเสร็จ และตรวจข้อมูลใน Supabase
- ไม่มีปุ่มเติมเครดิตทดลอง และไม่มีบัญชี admin/member ตัวอย่างสร้างบนฐานใหม่
- redeploy แล้วข้อมูลธุรกรรมยังอยู่; ตรวจ session ตามการตั้งค่า volume

## การตรวจสอบในเครื่อง

```powershell
dotnet publish CourtBook.csproj -c Release --no-restore -o artifacts/deploy-publish
python tests/deploy_smoke.py
```

การทดสอบใช้ published Production build และฐาน SQLite แยกเพื่อพิสูจน์สองพอร์ทัล Bootstrap การปิดเครดิตทดลอง health checks และการเก็บ Data Protection keys ไม่ได้ยืนยัน Docker image build หรือ Supabase ปลายทางจริง เครื่องปัจจุบันไม่มี Docker CLI จึงยังไม่ได้ build/run container จริง

แหล่งอ้างอิง: [Docker on Render](https://render.com/docs/docker), [Render Web Services](https://render.com/docs/web-services), [Render Blueprints](https://render.com/docs/blueprint-spec), [ASP.NET Core behind proxies](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)
