"""End-to-end tests against the real app and an isolated SQLite database. Python standard library only."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from html.parser import HTMLParser
from pathlib import Path
import copy
import base64
import datetime as dt
import html
import http.cookiejar
import json
import os
import re
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DLL = Path(os.environ.get("COURTBOOK_TEST_DLL", ROOT / "bin/Debug/net10.0/CourtBook.dll"))
PASSWORD = "Member-test-123!"
TODAY = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=7)).date()


class Tokens(HTMLParser):
    token = None
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("name") == "__RequestVerificationToken": self.token = a["value"]


class Browser:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
    def clone(self):
        result = Browser(self.base)
        for cookie in self.cookies: result.cookies.set_cookie(copy.copy(cookie))
        return result
    def request(self, route, fields=None):
        data = None if fields is None else urllib.parse.urlencode(fields, doseq=True).encode()
        try: response = self.opener.open(self.base + route, data=data, timeout=20)
        except urllib.error.HTTPError as error: response = error
        with response:
            return response.status, urllib.parse.urlsplit(response.url).path, response.read().decode()
    def token(self, route):
        status, _, body = self.request(route)
        assert status == 200, (route, status, body[:300])
        parser = Tokens(); parser.feed(body)
        assert parser.token, f"No CSRF token: {route}"
        return parser.token
    def post(self, route, fields, token_page=None):
        return self.request(route, {**fields, "__RequestVerificationToken": self.token(token_page or route)})
    def login(self, email, password=PASSWORD):
        return self.post("/Account/Login", {"Email":email,"Password":password})
    def upload(self, route, fields, image, filename="court.png", mime="image/png", csrf=True, file_field="Image", token_page=None):
        boundary="courtbook-"+os.urandom(12).hex()
        values=dict(fields)
        if csrf: values["__RequestVerificationToken"]=self.token(token_page or route)
        parts=[]
        for name,value in values.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; filename="{filename}"\r\nContent-Type: {mime}\r\n\r\n'.encode()+image+b"\r\n")
        parts.append(f'--{boundary}--\r\n'.encode())
        request=urllib.request.Request(self.base+route,data=b"".join(parts),headers={"Content-Type":f"multipart/form-data; boundary={boundary}"})
        try: response=self.opener.open(request,timeout=20)
        except urllib.error.HTTPError as error: response=error
        with response: return response.status,urllib.parse.urlsplit(response.url).path,response.read().decode()
    def quote(self, court, date, hours, promo=None):
        values={"CourtId":court,"Date":str(date),"Hours":hours}
        if promo is not None: values["PromotionId"]=promo
        status,_,body=self.request("/Bookings/Quote?"+urllib.parse.urlencode(values,doseq=True))
        return status,json.loads(body)


def check(condition, message):
    assert condition, message
    print("PASS:", message, flush=True)


def legacy_payment(database,booking_id,image,reference=None):
    """Restore a pre-wallet proof explicitly; retired customer endpoints cannot create one."""
    import hashlib
    reference=reference or 'SLIP-'+hashlib.sha256(image).hexdigest().upper()
    with closing(sqlite3.connect(database)) as connection:
        connection.execute('UPDATE Bookings SET PaymentSubmittedAt=?,TransferReference=?,SlipData=?,SlipContentType=? WHERE Id=?',
                           (dt.datetime.now(dt.timezone.utc).isoformat(),reference,image,'image/png',booking_id))
        connection.commit()


def fund_and_pay(browser,booking_id,amount=10000):
    """Use the enabled demo top-up in isolated tests, then pay via the real wallet endpoint."""
    _,checkout,_=browser.post('/Wallet/StartDemoPayment',{'Amount':amount},token_page='/Wallet')
    payment_id=checkout.rsplit('/',1)[1]
    browser.post('/Wallet/CompleteDemoPayment',{'id':payment_id,'outcome':'Paid'},token_page=checkout)
    return browser.post(f'/Bookings/PayWallet/{booking_id}',{},token_page=f'/Bookings/Result/{booking_id}')


def run():
    assert DLL.exists(), "Run dotnet build first"
    with tempfile.TemporaryDirectory(prefix="courtbook-tests-") as temporary:
        db = Path(temporary)/"courtbook.db"
        with socket.socket() as sock:
            sock.bind(("127.0.0.1",0)); port=sock.getsockname()[1]
        base=f"http://127.0.0.1:{port}"
        with socket.socket() as sock:
            sock.bind(("127.0.0.1",0)); admin_port=sock.getsockname()[1]
        admin_base=f"http://127.0.0.1:{admin_port}"
        extra_ports=[]
        while len(extra_ports)<2:
            with socket.socket() as sock:
                sock.bind(("127.0.0.1",0));candidate=sock.getsockname()[1]
            if candidate not in [port,admin_port,*extra_ports]:extra_ports.append(candidate)
        extra_bases=[f"http://127.0.0.1:{p}" for p in extra_ports]
        environment={**os.environ,"ASPNETCORE_ENVIRONMENT":"Testing","Database__Provider":"Sqlite","Database__Path":str(db),
                     "Logging__LogLevel__Default":"Warning","Portals__AdminPort":str(admin_port)}
        server=None
        def query(sql, args=()):
            with closing(sqlite3.connect(db)) as connection: return connection.execute(sql,args).fetchall()
        with (Path(temporary)/"server.log").open("w",encoding="utf-8") as log:
            def start():
                nonlocal server
                server=subprocess.Popen(["dotnet",str(DLL),"--urls",";".join([base,admin_base,*extra_bases])],cwd=ROOT,env=environment,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(100):
                    if server.poll() is not None: raise RuntimeError("Server exited")
                    try:
                        if Browser(base).request("/")[0]==200:return
                    except (OSError,urllib.error.URLError):pass
                    time.sleep(.1)
                raise RuntimeError("Server did not become ready")
            def stop():
                if server and server.poll() is None:
                    server.terminate()
                    try: server.wait(timeout=10)
                    except subprocess.TimeoutExpired: server.kill();server.wait()
            try:
                start()
                guest=Browser(base)
                check(all(guest.request(p)[0]==200 for p in ["/","/Courts","/Home/Promotions","/Account/Login","/Account/Register"]),"Public pages work without login")
                check('class="customer-portal"' in guest.request('/')[2],"Customer localhost opens the customer layout")
                check(all(guest.request(p)[0]==404 for p in ['/Admin','/admin','/ADMIN/Customers','/aDmIn/Bookings']),"Customer port blocks admin routes regardless of letter casing")
                check(guest.request("/Bookings/Create")[1]=="/Account/Login","Booking requires login")
                registration={"Name":"สมาชิกทดสอบ","Email":"test@example.com","Password":PASSWORD,"ConfirmPassword":PASSWORD,"Role":"Admin"}
                check(guest.request("/Account/Register",registration)[0]==400,"CSRF is required on registration")
                for bad in [{**registration,"Email":"invalid"},{**registration,"ConfirmPassword":"wrong"},{**registration,"Name":" "}]:
                    check(guest.post("/Account/Register",bad)[1]=="/Account/Register","Invalid registration is rejected")
                check(guest.post("/Account/Register",registration)[1]=="/Account/Login","Registration creates an account")
                check("อีเมลนี้มีบัญชี" in html.unescape(guest.post("/Account/Register",{**registration,"Email":" TEST@example.com "})[2]),"Duplicate email is case-insensitive")
                check(query("SELECT Role FROM Users WHERE Email='test@example.com'")[0][0]=="Member","Registration cannot grant admin role")
                check(guest.login("test@example.com","incorrect")[1]=="/Account/Login","Wrong password rejected")
                check(guest.login("test@example.com")[1]=="/Courts","Member login succeeds")
                member=guest
                check(member.request("/Admin")[0]==404,"Customer port does not expose admin pages")
                check(member.login("admin@courtbook.local","CourtBook123!")[1]=="/Account/Login","Customer login rejects admin accounts")
                admin=Browser(admin_base)
                check(admin.request("/")[1]=="/Account/Login","Admin root opens admin login")
                admin_login=admin.request('/account/login')
                check(admin_login[0]==200 and 'class="admin-portal"' in admin_login[2] and 'name="ConfirmPassword"' not in admin_login[2],"Admin localhost opens its own login layout with lowercase routes")
                check(all(admin.request(p)[0]==404 for p in ['/courts','/BOOKINGS/History','/account/register','/home/index']),"Admin port blocks customer routes regardless of letter casing")
                check(all(admin.request(p)[0]==404 for p in ["/Courts","/Bookings/Create","/Account/Register"]),"Admin port does not expose customer pages or registration")
                check(admin.login("test@example.com")[1]=="/Account/Login","Admin login rejects customer accounts")
                check(admin.login("admin@courtbook.local","CourtBook123!")[1]=="/Admin","Admin login succeeds")
                for (equipment_id,) in query('SELECT Id FROM RentalEquipment'):
                    admin.post('/Admin/EquipmentStock/'+equipment_id,{'TotalQuantity':50,'RepairQuantity':0,'Revision':0,'Note':'Counted test inventory fixture'})
                shared=member.clone();shared.base=admin_base
                check(shared.request("/Admin")[1]=="/Account/Login","Customer cookie cannot authenticate on admin port")
                reverse=admin.clone();reverse.base=base
                check(reverse.request('/Bookings/History')[1]=='/Account/Login',"Admin cookie cannot authenticate on customer port")
                for cookie in admin.cookies: member.cookies.set_cookie(copy.copy(cookie))
                check(member.request("/Account/Profile")[1]=="/Account/Profile","Admin login preserves customer session in same browser")
                check(all(admin.request(p)[0]==200 for p in ["/Admin","/Admin/Courts","/Admin/Court/0","/Admin/Schedule","/Admin/Bookings","/Admin/Promotions","/Admin/Promotion/0"]),"All admin pages work")
                # A single cookie jar models tabs in one browser, including all customer ports.
                multi=member.clone()
                for index,customer_base in enumerate(extra_bases,2):
                    multi.base=customer_base
                    check(multi.request('/Account/Profile')[1]=='/Account/Login',f"Customer port {index} does not inherit another port's login")
                    email=f"customer-port-{index}@example.com"
                    multi.post('/Account/Register',{**registration,'Name':f'Customer {index}','Email':email})
                    check(multi.login(email)[1]=='/Courts',f"Customer {index} signs in independently in same browser")
                for customer_base,email in [(base,'test@example.com'),*zip(extra_bases,['customer-port-2@example.com','customer-port-3@example.com'])]:
                    multi.base=customer_base
                    status,path,profile=multi.request('/Account/Profile')
                    check(status==200 and email in profile,"Each of three customer ports preserves its own account")
                multi.base=extra_bases[0]
                multi.post('/Account/Logout',{},token_page='/Account/Profile')
                check(multi.request('/Account/Profile')[1]=='/Account/Login',"Logout clears only the selected customer port")
                for customer_base,email in [(base,'test@example.com'),(extra_bases[1],'customer-port-3@example.com')]:
                    multi.base=customer_base
                    check(email in multi.request('/Account/Profile')[2],"Other customer accounts remain signed in after one port logs out")
                multi.base=admin_base
                check(multi.request('/Admin')[1]=='/Admin',"Admin login remains independent of all customer logouts")
                check(all(Browser(customer_base).request('/Admin')[0]==404 for customer_base in extra_bases),"Every extra customer port blocks admin pages")
                check(query("SELECT COUNT(*) FROM Promotions")[0][0]==5,"Five initial promotions exist")
                thai_now=dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=7)
                elapsed=[h for h in range(8,22) if h<=thai_now.hour]
                today=thai_now.date()
                if elapsed:
                    today_body=member.request(f"/Bookings/Create?courtId=1&date={today}")[2]
                    tags=re.findall(r'<input\b[^>]*name="Hours"[^>]*>',today_body)
                    disabled={int(re.search(r'value="(\d+)"',tag).group(1)) for tag in tags if 'disabled=' in tag}
                    check(set(elapsed)<=disabled,"Today's elapsed hours are disabled in booking page")
                    check(all(member.quote(1,today,[h])[0]==400 for h in elapsed),"Every elapsed hour today is rejected by server")
                    check(member.post("/Bookings/Create",{"CourtId":1,"Date":str(today),"Hours":[elapsed[-1]]})[1]=="/Bookings/Create" and query("SELECT COUNT(*) FROM Bookings")[0][0]==0,"Forged submission cannot book an elapsed hour")
                check(all(member.quote(1,today-dt.timedelta(days=1),[h])[0]==400 for h in range(8,22)),"All hours on a past day are unbookable")
                for route in ['/Courts','/Bookings/Create?courtId=1']:
                    date_input=re.search(r'<input\b[^>]*type="date"[^>]*>',member.request(route)[2]).group(0)
                    check(f'min="{today}"' in date_input,'Date picker prevents choosing a past day: '+route)
                previous_count=query('SELECT COUNT(*) FROM Bookings')[0][0]
                member.post('/Bookings/Create',{'CourtId':1,'Date':str(today-dt.timedelta(days=1)),'Hours':[9]})
                check(query('SELECT COUNT(*) FROM Bookings')[0][0]==previous_count,'Forged past-date POST cannot create a booking')
                past_schedule=member.request('/Courts?courtId=1&date='+str(today-dt.timedelta(days=1)))[2]
                check('class="slot available"' not in past_schedule,'Opening an old schedule URL offers no bookable slots')
                check(member.quote(1,today+dt.timedelta(days=1),[8])[0]==200,"Same hour on a future day remains bookable")
                # ISO dates from HTML forms must stay Gregorian even on Thai Windows.
                calendar_member=Browser(base)
                calendar_member.post("/Account/Register",{**registration,"Email":"calendar@example.com"})
                calendar_member.login("calendar@example.com")
                calendar_date=TODAY+dt.timedelta(days=2)
                for invalid_date in ["not-a-date", "2026-02-30", ""]:
                    status,path,body=calendar_member.post("/Bookings/Create",{"CourtId":2,"Date":invalid_date,"Hours":[21]})
                    check(status==200 and path=="/Bookings/Create" and "วันที่ไม่ถูกต้อง" in html.unescape(body),"Invalid form date is rejected")
                status,path,body=calendar_member.post("/Bookings/Create",{"CourtId":2,"Date":str(calendar_date),"Hours":[21]})
                check(status==200 and "/Bookings/Result/" in path,"ISO form date accepts a future one-hour booking")
                check(query("SELECT PlayDate FROM Bookings WHERE Id=?",(int(path.rsplit("/",1)[1]),))[0][0]==str(calendar_date),"Form date is stored with the submitted Gregorian year")
                calendar_booking_id=int(path.rsplit("/",1)[1])
                weekday=TODAY+dt.timedelta(days=8)
                while weekday.weekday()>4:weekday+=dt.timedelta(days=1)
                for promo,hours,expected in [(1,[9],(20000,5000,15000)),(2,[9,10,11],(60000,9000,51000)),
                                             (3,[8,9,10,11],(80000,8000,72000)),(4,[14],(20000,8000,12000)),
                                             (5,[8,9,10,11,12],(100000,10000,90000))]:
                    status,data=member.quote(1,weekday,hours,promo)
                    check(status==200 and tuple(data[k] for k in ["grossSatang","discountSatang","netSatang"])==expected,f"Promotion {promo}: exact discount calculation")
                    promo_member=Browser(base)
                    promo_email=f"promo-{promo}@example.com"
                    promo_member.post("/Account/Register",{**registration,"Email":promo_email});promo_member.login(promo_email)
                    promo_date=weekday+dt.timedelta(days=7*promo)
                    _,promo_result,_=promo_member.post("/Bookings/Create",{"CourtId":2,"Date":str(promo_date),"Hours":hours,"PromotionId":promo})
                    promo_booking=int(promo_result.rsplit("/",1)[1])
                    check(query("SELECT GrossSatang,DiscountSatang,NetSatang FROM Bookings WHERE Id=?",(promo_booking,))[0]==expected,f"Promotion {promo}: saved booking keeps exact totals")
                    pending_qr=promo_member.request(promo_result)[2]
                    check("payment-qr.jpg" not in pending_qr and 'id="wallet-payment"' in pending_qr,f"Promotion {promo}: wallet payment is available while approval is pending")
                    if promo==1:
                        pending_slip=base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5WQAAAAASUVORK5CYII=")
                        check(promo_member.upload(f"/Bookings/SubmitPayment/{promo_booking}",{"TransferReference":"PENDING-TX"},pending_slip,file_field="Slip",token_page=promo_result)[0]==404,"New booking transfers are retired")
                        legacy_payment(db,promo_booking,pending_slip,'PENDING-TX')
                        check(query("SELECT Status,PaidAt,TransferReference FROM Bookings WHERE Id=?",(promo_booking,))[0]==("Pending",None,"PENDING-TX"),"Submitting proof does not approve booking or confirm funds")
                    promo_detail=f"/Admin/Booking/{promo_booking}"
                    admin.post(f"/Admin/ConfirmBooking/{promo_booking}",{},token_page=promo_detail)
                    qr_body=promo_member.request(promo_result)[2]
                    check(('wallet-payment' in qr_body if promo!=1 else "รอตรวจสอบการชำระเงิน" in html.unescape(qr_body)) and f"{expected[2]/100:,.2f}" in qr_body,f"Promotion {promo}: wallet or historical proof displays the saved discounted amount")
                check(member.quote(1,weekday,[11,12],3)[0]==400,"Morning promo rejects hours after noon")
                check(member.quote(1,TODAY+dt.timedelta(days=6),[14],4)[0]==400,"Advance promo requires 7 calendar days")
                check(member.quote(1,weekday,[9,10],2)[0]==400,"Consecutive promo requires 3 hours")
                check(member.quote(1,weekday,[9,10,11,12],5)[0]==400,"Threshold promo requires 1000 baht")
                for hours in [[],[9,11],[9,9],[7],[22]]:
                    check(member.quote(1,weekday,hours)[0]==400,"Invalid or non-contiguous hours rejected")
                check(member.quote(1,TODAY-dt.timedelta(days=1),[9])[0]==400,"Past booking rejected")
                schedule={"courtId":1,"date":str(weekday),"blocked":[9]}
                check(admin.post("/Admin/Schedule",schedule,token_page=f"/Admin/Schedule?courtId=1&date={weekday}")[1]=="/Admin/Schedule","Admin blocks a time slot")
                check(member.quote(1,weekday,[9])[0]==400,"Closed slot cannot be booked")
                admin.post("/Admin/Schedule",{**schedule,"blocked":[]},token_page=f"/Admin/Schedule?courtId=1&date={weekday}")
                check(member.quote(1,weekday,[9])[0]==200,"Reopened slot can be booked")
                booking={"CourtId":1,"Date":str(weekday),"Hours":[9,10,11],"PromotionId":2,"NetSatang":1,"GrossSatang":1}
                check(member.request("/Bookings/Create",booking)[0]==400,"CSRF required for booking")
                status,path,body=member.post("/Bookings/Create",booking,token_page=f"/Bookings/Create?courtId=1&date={weekday}")
                check(status==200 and "/Bookings/Result/" in path,"Booking confirmation succeeds")
                booking_id=int(path.rsplit("/",1)[1])
                check(query("SELECT GrossSatang,DiscountSatang,NetSatang FROM Bookings WHERE Id=?",(booking_id,))[0]==(60000,9000,51000),"Server ignores forged totals and saves 510 baht")
                check("510.00" in body,"Result shows saved net total")
                check(query("SELECT Status,PaidAt FROM Bookings WHERE Id=?",(booking_id,))[0]==("Pending",None),"New booking waits for approval and payment")
                check("รอผู้ดูแลยืนยัน" in html.unescape(body),"Customer sees pending approval")
                detail=f"/Admin/Booking/{booking_id}"
                confirm=f"/Admin/ConfirmBooking/{booking_id}"
                payment=f"/Admin/ConfirmPayment/{booking_id}"
                check(admin.request(confirm,{})[0]==400,"CSRF required for admin confirmation")
                check(member.request(confirm,{})[0]==404,"Customer port cannot confirm a booking")
                check(admin.post(payment,{"Reference":"early"},token_page=detail)[1]==payment and query("SELECT PaidAt FROM Bookings WHERE Id=?",(booking_id,))[0][0] is None,"Payment requires booking approval first")
                check(admin.post(confirm,{},token_page=detail)[1]==detail,"Admin confirms pending booking")
                approval=query("SELECT Status,ConfirmedAt,ConfirmedBy FROM Bookings WHERE Id=?",(booking_id,))[0]
                check(approval[0]=="Confirmed" and approval[1] is not None and approval[2]==1,"Approval records admin identity and timestamp")
                pending_image=base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5WQAAAAASUVORK5CYII=")
                submit_payment=f"/Bookings/SubmitPayment/{booking_id}"
                customer_detail=f"/Bookings/Result/{booking_id}"
                with member.opener.open(base+"/images/payment-qr.jpg") as response:
                    check(response.read()==(ROOT/"wwwroot/images/payment-qr.jpg").read_bytes(),"QR is served unchanged from original image asset")
                check(member.upload(submit_payment,{"TransferReference":"TX-510"},pending_image,csrf=False,file_field="Slip")[0]==404,"Retired booking proof endpoint is unavailable")
                for reference,slip in [("x"*201,pending_image),("TX-510",b"<svg/>")]:
                    member.upload(submit_payment,{"TransferReference":reference},slip,file_field="Slip",token_page=customer_detail)
                    check(query("SELECT PaymentSubmittedAt FROM Bookings WHERE Id=?",(booking_id,))[0][0] is None,"Invalid transfer reference or slip is rejected")
                check(member.upload(submit_payment,{"TransferReference":"TX-510","NetSatang":1,"PaidAt":"forged"},pending_image,file_field="Slip",token_page=customer_detail)[0]==404,"Customer cannot submit new direct transfer proof")
                legacy_payment(db,booking_id,pending_image,'TX-510')
                check(query("SELECT PaidAt,TransferReference,SlipData,NetSatang FROM Bookings WHERE Id=?",(booking_id,))[0]==(None,"TX-510",pending_image,51000),"Slip submission cannot mark payment confirmed or change amount")
                for client,slip_route in [(member,f"/Bookings/PaymentSlip/{booking_id}"),(admin,f"/Admin/PaymentSlip/{booking_id}")]:
                    with client.opener.open(client.base+slip_route) as response:
                        check(response.read()==pending_image and response.headers["Cache-Control"]=="no-store","Only authorized slip endpoint serves proof without caching")
                check(Browser(base).request(f"/Bookings/PaymentSlip/{booking_id}")[1]=="/Account/Login","Guest cannot view payment slip")
                check("รอตรวจสอบการชำระเงิน" in html.unescape(member.request(customer_detail)[2]),"Customer sees proof waiting for manual review")
                member.post(f"/Bookings/Cancel/{booking_id}",{},token_page=customer_detail)
                check(query("SELECT Status FROM Bookings WHERE Id=?",(booking_id,))[0][0]=="Confirmed","Submitted transfer requires admin review before cancellation")
                admin.post(confirm,{},token_page=detail)
                check(query("SELECT Status,ConfirmedAt,ConfirmedBy FROM Bookings WHERE Id=?",(booking_id,))[0]==approval,"Repeated approval preserves original audit")
                for reference in ["", " ", "x"*201]:
                    admin.post(payment,{"Reference":reference},token_page=detail)
                    check(query("SELECT PaidAt FROM Bookings WHERE Id=?",(booking_id,))[0][0] is None,"Payment requires a valid receipt reference")
                check(admin.post(payment,{"Reference":"CASH-510","NetSatang":1},token_page=detail)[1]==detail,"Admin confirms receipt of booking total")
                receipt=query("SELECT PaidAt,PaidBy,PaymentReference,NetSatang FROM Bookings WHERE Id=?",(booking_id,))[0]
                check(receipt[0] is not None and receipt[1:]==(1,"CASH-510",51000),"Payment records admin and reference without changing saved total")
                admin.post(payment,{"Reference":"overwrite"},token_page=detail)
                check(query("SELECT PaidAt,PaidBy,PaymentReference,NetSatang FROM Bookings WHERE Id=?",(booking_id,))[0]==receipt,"Repeated payment cannot overwrite receipt")
                customer_body=html.unescape(member.request(f"/Bookings/Result/{booking_id}")[2])
                check("ยืนยันชำระเงินแล้ว" in customer_body and "CASH-510" in customer_body,"Customer sees admin approval and payment receipt")
                check("payment-qr.jpg" not in customer_body,"Paid booking hides QR to avoid another transfer")
                user_id=query("SELECT Id FROM Users WHERE Email='test@example.com'")[0][0]
                customer=admin.request(f"/Admin/Customer/{user_id}")
                check(customer[0]==200 and "test@example.com" in customer[2] and f"CB{booking_id:06d}" in customer[2],"Admin sees customer account and booking history")
                check(admin.request("/Admin/Customers")[0]==200 and "PasswordHash" not in customer[2],"Customer list excludes password data")
                check(admin.request("/Admin/Customer/1")[0]==404,"Customer account pages exclude admin accounts")
                check(admin.post("/Admin/SportTypes",{"Name":"Tennis"})[1]=="/Admin/SportTypes","Admin adds a sport type")
                for name in ["tennis", "", " ", "x"*101]:
                    admin.post("/Admin/SportTypes",{"Name":name})
                check(query("SELECT COUNT(*) FROM SportTypes")[0][0]==2,"Sport types reject duplicates and invalid names")
                typed_court={"Id":2,"Name":"สนาม B","Description":"Tennis court","Price":200,"IsActive":"true","SportTypeId":2}
                check(admin.post("/Admin/Court/2",typed_court)[1]=="/Admin/Courts","Admin assigns sport type to a court")
                check("Tennis" in member.request("/Courts?courtId=2")[2],"Customer sees assigned sport type")
                admin.post("/Admin/Court/2",{**typed_court,"SportTypeId":9999})
                check(query("SELECT SportTypeId FROM Courts WHERE Id=2")[0][0]==2,"Unknown sport type cannot change a court")
                png=base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5WQAAAAASUVORK5CYII=")
                check(admin.upload("/Admin/Court/2",typed_court,png,csrf=False)[0]==400,"Image upload requires CSRF")
                check(member.upload("/Admin/Court/2",typed_court,png,csrf=False)[0]==404,"Customer cannot upload court images")
                check(admin.upload("/Admin/Court/2",typed_court,png,filename="court.php",mime="text/html")[1]=="/Admin/Courts","Admin uploads court image using detected image type")
                saved_image=query("SELECT ImageData,ImageContentType,ImageVersion FROM Courts WHERE Id=2")[0]
                check(saved_image[0]==png and saved_image[1]=="image/png" and bool(saved_image[2]),"Image bytes and detected MIME are stored in database")
                for client in [member,admin]:
                    with client.opener.open(client.base+"/Courts/Image/2") as response:
                        check(response.read()==png and response.headers.get_content_type()=="image/png" and response.headers["X-Content-Type-Options"]=="nosniff","Stored photo is served correctly on customer and admin ports")
                check("/Courts/Image/2" in member.request("/")[2] and "/Courts/Image/2" in member.request("/Courts?courtId=2")[2] and "/Courts/Image/2" in member.request("/Bookings/Create?courtId=2")[2],"Photo appears on home, court schedule and booking page")
                admin.post("/Admin/Court/2",typed_court)
                check(query("SELECT ImageData,ImageContentType,ImageVersion FROM Courts WHERE Id=2")[0]==saved_image,"Editing court details without upload preserves photo")
                for invalid in [b"<svg onload='alert(1)'></svg>",b"",b"x"*(5*1024*1024+1)]:
                    check(admin.upload("/Admin/Court/2",{**typed_court,"Name":"invalid upload"},invalid)[1]=="/Admin/Court/2","Unsupported or oversized image is rejected")
                    check(query("SELECT Name,ImageVersion FROM Courts WHERE Id=2")[0]==("สนาม B",saved_image[2]),"Rejected upload preserves court and existing photo")
                admin.upload("/Admin/Court/2",typed_court,png)
                check(query("SELECT ImageVersion FROM Courts WHERE Id=2")[0][0]!=saved_image[2],"Replacing photo changes its URL version")
                admin.post("/Admin/Court/2",{**typed_court,"RemoveImage":"true"})
                check(query("SELECT ImageData,ImageContentType,ImageVersion FROM Courts WHERE Id=2")[0]==(None,None,""),"Removing photo clears image data")
                check(member.request("/Courts/Image/2")[0]==404,"Removed photo endpoint returns not found")
                admin.upload("/Admin/Court/2",typed_court,png)
                check(member.quote(1,weekday,[10])[0]==400,"Overlap is blocked")
                check(member.quote(1,weekday,[12])[0]==200,"Adjacent hour is still available")
                check(member.quote(2,weekday,[9],1)[0]==400,"First-booking discount cannot be reused")
                blocked=admin.post("/Admin/Schedule",schedule,token_page=f"/Admin/Schedule?courtId=1&date={weekday}")[2]
                check("ไม่สามารถปิดทับ" in html.unescape(blocked),"Admin cannot close a booked slot")
                second=Browser(base)
                second.post("/Account/Register",{**registration,"Email":"second@example.com"})
                second.login("second@example.com")
                check(second.request(f"/Bookings/PaymentSlip/{booking_id}")[0]==404,"Other customers cannot view a payment slip")
                check(second.upload(f"/Bookings/SubmitPayment/{booking_id}",{"TransferReference":"forged"},pending_image,file_field="Slip",token_page="/Bookings/History")[0]==404,"Other customers cannot replace transfer proof")
                cancel=f"/Bookings/Cancel/{calendar_booking_id}"
                calendar_detail=f"/Bookings/Result/{calendar_booking_id}"
                check(calendar_member.request(cancel)[0]==405,"Cancellation requires POST")
                check(calendar_member.request(cancel,{})[0]==400,"Cancellation requires CSRF token")
                check(second.post(cancel,{},token_page="/Bookings/History")[0]==404,"Customers cannot cancel another account's booking")
                check(calendar_member.post(cancel,{},token_page=calendar_detail)[1]==calendar_detail,"Customer cancels own pending booking")
                cancellation=query("SELECT Status,CancelledAt FROM Bookings WHERE Id=?",(calendar_booking_id,))[0]
                check(cancellation[0]=="Cancelled" and cancellation[1] is not None and query("SELECT COUNT(*) FROM BookedSlots WHERE BookingId=?",(calendar_booking_id,))[0][0]==0,"Cancellation records audit and releases reserved hours")
                check("payment-qr.jpg" not in calendar_member.request(calendar_detail)[2],"Cancelled booking does not offer QR payment")
                check(calendar_member.quote(2,calendar_date,[21],1)[0]==200,"Cancelled first booking restores unused new-member eligibility")
                check("ยกเลิกแล้ว" in html.unescape(admin.request(f"/Admin/Booking/{calendar_booking_id}")[2]),"Admin sees customer cancellation")
                admin.post(f"/Admin/ConfirmBooking/{calendar_booking_id}",{},token_page=f"/Admin/Booking/{calendar_booking_id}")
                admin.post(f"/Admin/ConfirmPayment/{calendar_booking_id}",{"Reference":"invalid"},token_page=f"/Admin/Booking/{calendar_booking_id}")
                check(query("SELECT Status,PaidAt FROM Bookings WHERE Id=?",(calendar_booking_id,))[0]==("Cancelled",None),"Cancelled booking cannot be approved or paid")
                replacement={"CourtId":2,"Date":str(calendar_date),"Hours":[21]}
                _,replacement_path,_=calendar_member.post("/Bookings/Create",replacement)
                replacement_id=int(replacement_path.rsplit("/",1)[1])
                calendar_member.post(cancel,{},token_page=calendar_detail)
                check(query("SELECT Status,CancelledAt FROM Bookings WHERE Id=?",(calendar_booking_id,))[0]==cancellation and query("SELECT BookingId FROM BookedSlots WHERE CourtId=2 AND PlayDate=? AND Hour=21",(str(calendar_date),))[0][0]==replacement_id,"Repeated cancellation preserves audit and cannot release replacement booking")
                admin.post(f"/Admin/ConfirmBooking/{replacement_id}",{},token_page=f"/Admin/Booking/{replacement_id}")
                calendar_member.post(f"/Bookings/Cancel/{replacement_id}",{},token_page=replacement_path)
                check(query("SELECT Status FROM Bookings WHERE Id=?",(replacement_id,))[0][0]=="Cancelled","Customer can cancel approved unpaid booking")
                member.post(f"/Bookings/Cancel/{booking_id}",{},token_page=f"/Bookings/Result/{booking_id}")
                check(query("SELECT Status,PaidAt FROM Bookings WHERE Id=?",(booking_id,))[0]==("Confirmed",receipt[0]) and member.quote(1,weekday,[10])[0]==400,"Paid booking cannot be cancelled or release its slot")
                _,expired_path,_=calendar_member.post("/Bookings/Create",replacement)
                expired_id=int(expired_path.rsplit("/",1)[1])
                with closing(sqlite3.connect(db)) as connection:
                    connection.execute("UPDATE Bookings SET PlayDate=? WHERE Id=?",(str(TODAY-dt.timedelta(days=1)),expired_id))
                    connection.execute("UPDATE BookedSlots SET PlayDate=? WHERE BookingId=?",(str(TODAY-dt.timedelta(days=1)),expired_id))
                    connection.commit()
                calendar_member.post(f"/Bookings/Cancel/{expired_id}",{},token_page=expired_path)
                check(query("SELECT Status FROM Bookings WHERE Id=?",(expired_id,))[0][0]=="Pending" and query("SELECT COUNT(*) FROM BookedSlots WHERE BookingId=?",(expired_id,))[0][0]==1,"Started booking cannot be cancelled")
                _,race_path,_=calendar_member.post("/Bookings/Create",replacement)
                cancel_race_id=int(race_path.rsplit("/",1)[1])
                race_detail=f"/Admin/Booking/{cancel_race_id}"
                admin.post(f"/Admin/ConfirmBooking/{cancel_race_id}",{},token_page=race_detail)
                fund_and_pay_target=calendar_member.post('/Wallet/StartDemoPayment',{'Amount':1000},token_page='/Wallet')[1]
                calendar_member.post('/Wallet/CompleteDemoPayment',{'id':fund_and_pay_target.rsplit('/',1)[1],'outcome':'Paid'},token_page=fund_and_pay_target)
                cancel_client=calendar_member.clone();pay_client=calendar_member.clone()
                cancel_payload={"__RequestVerificationToken":cancel_client.token(race_path)}
                pay_payload={"__RequestVerificationToken":pay_client.token(race_path)}
                with ThreadPoolExecutor(max_workers=2) as pool:
                    cancellation_future=pool.submit(cancel_client.request,f"/Bookings/Cancel/{cancel_race_id}",cancel_payload)
                    payment_future=pool.submit(pay_client.request,f"/Bookings/PayWallet/{cancel_race_id}",pay_payload)
                    check(cancellation_future.result()[0]==200 and payment_future.result()[0]==200,"Concurrent cancellation and payment are handled")
                race_status,race_paid=query("SELECT Status,PaidAt FROM Bookings WHERE Id=?",(cancel_race_id,))[0]
                race_slots=query("SELECT COUNT(*) FROM BookedSlots WHERE BookingId=?",(cancel_race_id,))[0][0]
                check((race_status=="Cancelled" and race_paid is None and race_slots==0) or (race_status=="Confirmed" and race_paid is not None and race_slots==1),"Cancellation and payment cannot both succeed for the same booking")
                check(second.request(f"/Bookings/Result/{booking_id}")[0]==404,"Members cannot view another member's booking")
                check(f"CB{booking_id:06d}" not in second.request("/Bookings/History?userId=3")[2],"History cannot be switched to another user")
                check(admin.request(f"/Admin/Booking/{booking_id}")[0]==200,"Admin can inspect booking details")
                court={"Id":1,"Name":"สนาม A เปลี่ยนชื่อ","Description":"ทดสอบราคาใหม่","Price":"350","IsActive":"true"}
                admin.post("/Admin/Court/1",court)
                check(query("SELECT PriceSatang,CourtName FROM Bookings WHERE Id=?",(booking_id,))[0]==(20000,"สนาม A"),"Booking snapshots survive later court edits")
                admin.post("/Admin/Court/1",{**court,"IsActive":"false"})
                check(member.quote(1,weekday,[14])[0]==400,"Closed court rejects bookings")
                admin.post("/Admin/Court/1",{**court,"Price":"200","IsActive":"true"})
                promo={"Id":1,"Name":"สมาชิกใหม่","Rule":"NewMember","Kind":"Fixed","Value":"50","MinHours":3,
                       "MinDays":7,"MinSpend":1000,"Starts":str(TODAY),"Ends":str(TODAY+dt.timedelta(days=365)),"IsActive":"false"}
                admin.post("/Admin/Promotion/1",promo)
                check(second.quote(2,weekday,[8],1)[0]==400,"Inactive promotion is rejected at confirmation")
                admin.post("/Admin/Promotion/1",{**promo,"IsActive":"true"})
                # Two sessions race for the same court/hour. The unique slot and transaction must yield one booking.
                race_date=weekday+dt.timedelta(days=1)
                clients=[member.clone(),second.clone()]
                payloads=[{"CourtId":2,"Date":str(race_date),"Hours":[15],"__RequestVerificationToken":b.token("/Bookings/Create")} for b in clients]
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results=list(pool.map(lambda pair:pair[0].request("/Bookings/Create",pair[1]),zip(clients,payloads)))
                check(sum("/Bookings/Result/" in result[1] for result in results)==1,"Concurrent overlapping bookings: exactly one succeeds")
                check(query("SELECT COUNT(*) FROM BookedSlots WHERE CourtId=2 AND PlayDate=? AND Hour=15",(str(race_date),))[0][0]==1,"No duplicate slot stored")
                # First-booking eligibility must also be serialized for distinct courts.
                fresh=Browser(base)
                fresh.post("/Account/Register",{**registration,"Email":"first-race@example.com"});fresh.login("first-race@example.com")
                clients=[fresh.clone(),fresh.clone()]
                payloads=[{"CourtId":i+1,"Date":str(race_date),"Hours":[18],"PromotionId":1,"__RequestVerificationToken":b.token("/Bookings/Create")} for i,b in enumerate(clients)]
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results=list(pool.map(lambda pair:pair[0].request("/Bookings/Create",pair[1]),zip(clients,payloads)))
                check(sum("/Bookings/Result/" in result[1] for result in results)==1,"First-booking promotion cannot be spent twice concurrently")
                # Add a cheap court to prove net totals never become negative.
                admin.upload("/Admin/Court/0",{"Id":0,"Name":"สนามราคาทดสอบ","Description":"precision","Price":"10","IsActive":"true"},png)
                check(query("SELECT ImageData FROM Courts WHERE Id=3")[0][0]==png,"New court can be created with an uploaded photo")
                clamp=Browser(base)
                clamp.post("/Account/Register",{**registration,"Email":"clamp@example.com"});clamp.login("clamp@example.com")
                status,data=clamp.quote(3,weekday,[9],1)
                check(status==200 and (data["grossSatang"],data["discountSatang"],data["netSatang"])==(1000,1000,0),"Discount is capped at gross and net never becomes negative")
                _,free_path,free_body=clamp.post("/Bookings/Create",{"CourtId":3,"Date":str(weekday),"Hours":[20],"PromotionId":1})
                check("/Bookings/Result/" in free_path and "payment-qr.jpg" not in free_body,"Zero-total booking does not ask for QR payment")
                admin.post("/Admin/Court/3",{"Id":3,"Name":"สนามราคาทดสอบ","Description":"precision","Price":"199.99","IsActive":"true"})
                custom={**promo,"Id":0,"Name":"ทดสอบทศนิยม","Rule":"Consecutive","Kind":"Percent","Value":"12.5","MinHours":1,"IsActive":"true"}
                check(admin.post("/Admin/Promotion/0",custom)[1]=="/Admin/Promotions","Admin adds a promotion")
                status,data=clamp.quote(3,weekday,[9],6)
                check(status==200 and (data["grossSatang"],data["discountSatang"],data["netSatang"])==(19999,2500,17499),"Money rounds to satang without floating-point drift")
                check("เปอร์เซ็นต์" in html.unescape(admin.post("/Admin/Promotion/6",{**custom,"Id":6,"Value":101})[2]),"Admin cannot save percentage above 100")
                admin.post("/Admin/Promotion/6",{**custom,"Id":6,"Starts":str(TODAY-dt.timedelta(days=2)),"Ends":str(TODAY-dt.timedelta(days=1))})
                check(clamp.quote(3,weekday,[9],6)[0]==400,"Expired promotion cannot be used")
                check(query("SELECT COUNT(*) FROM Users WHERE PasswordHash LIKE '%Member-test-123!%'")[0][0]==0,"Passwords stored as hashes")
                shared_logout=member.clone();shared_logout.base=admin_base
                shared_logout.post('/Account/Logout',{},token_page='/Admin')
                shared_logout.base=base
                check(shared_logout.request('/Bookings/History')[1]=='/Bookings/History',"Logging out of admin portal preserves customer login")
                # Equipment administration and sport isolation use the same real booking endpoints.
                check(admin.request('/Admin/Equipment')[0]==200 and admin.request('/Admin/EquipmentItem')[0]==200,"Admin can open equipment management and editor")
                check(member.request('/Admin/Equipment')[0]==404,"Customer portal cannot manage equipment")
                check(admin.request('/Admin/EquipmentItem',{})[0]==400,"Equipment changes require CSRF")
                sport_id=query("SELECT SportTypeId FROM Courts WHERE Id=2")[0][0]
                isolated_date=TODAY+dt.timedelta(days=90)
                foreign={"CourtId":2,"Date":str(isolated_date),"Hours":[8],"Equipment[racket]":1}
                check('name="Equipment[racket]"' not in member.request('/Bookings/Create?courtId=2')[2],"Tennis court hides badminton equipment")
                status,_,_=member.request('/Bookings/Quote?'+urllib.parse.urlencode(foreign,doseq=True))
                check(status==400 and member.post('/Bookings/Create',foreign)[1]=='/Bookings/Create',"Quote and booking reject equipment from another sport")
                equipment_form={"Id":"","SportTypeId":sport_id,"Name":"ไม้เทนนิสทดสอบ","Unit":"อัน","Price":"65.50","IsActive":"true","InitialStock":20}
                check(admin.post('/Admin/EquipmentItem',equipment_form,token_page='/Admin/EquipmentItem')[1]=='/Admin/Equipment',"Admin creates sport-specific rental equipment")
                equipment_id=query("SELECT Id FROM RentalEquipment WHERE Name=?",(equipment_form['Name'],))[0][0]
                equipment_editor='/Admin/EquipmentItem?id='+equipment_id
                check('name="Equipment['+equipment_id+']"' in member.request('/Bookings/Create?courtId=2')[2],"New tennis equipment appears only for matching court sport")
                check('name="Equipment['+equipment_id+']"' not in member.request('/Bookings/Create?courtId=1')[2],"Badminton court hides tennis equipment")
                tennis_fields={"CourtId":2,"Date":str(isolated_date),"Hours":[8],f"Equipment[{equipment_id}]":2}
                _,tennis_path,tennis_body=member.post('/Bookings/Create',tennis_fields)
                check('/Bookings/Result/' in tennis_path,"Matching equipment can be reserved")
                tennis_id=int(tennis_path.rsplit('/',1)[1])
                tennis_snapshot=query('SELECT EquipmentJson,NetSatang FROM Bookings WHERE Id=?',(tennis_id,))[0]
                check(tennis_snapshot[1]==33100,"Sport-specific rental uses current server price")
                admin.post('/Admin/EquipmentItem',{**equipment_form,"Id":equipment_id,"Name":"ชื่ออุปกรณ์ใหม่","Price":"99","IsActive":"false"},token_page=equipment_editor)
                check('name="Equipment['+equipment_id+']"' not in member.request('/Bookings/Create?courtId=2')[2],"Disabled equipment is hidden")
                check(member.post('/Bookings/Create',{**tennis_fields,"Hours":[10]})[1]=='/Bookings/Create',"Disabled equipment cannot be booked through a forged request")
                check(query('SELECT EquipmentJson,NetSatang FROM Bookings WHERE Id=?',(tennis_id,))[0]==tennis_snapshot and 'ไม้เทนนิสทดสอบ' in html.unescape(member.request(tennis_path)[2]),"Equipment edits preserve existing booking names and prices")
                check('validation-summary-errors' in admin.post('/Admin/EquipmentItem',{**equipment_form,"SportTypeId":999999},token_page='/Admin/EquipmentItem')[2],"Equipment cannot use an unknown sport")
                check('validation-summary-errors' in admin.post('/Admin/EquipmentItem',{**equipment_form,"Price":-1},token_page='/Admin/EquipmentItem')[2],"Equipment rejects a negative rental price")
                # Equipment prices come from the server and are snapshotted with the booking.
                rental_date=TODAY+dt.timedelta(days=30)
                rental={"CourtId":1,"Date":str(rental_date),"Hours":[8,9],"Equipment[racket]":2,"Equipment[shoes]":1}
                def rental_quote(fields):
                    status,_,body=member.request("/Bookings/Quote?"+urllib.parse.urlencode(fields,doseq=True))
                    return status,json.loads(body)
                rental_page=html.unescape(member.request("/Bookings/Create")[2])
                check('name="Equipment[racket]"' in rental_page and 'id="equipment-total"' in rental_page,"Date/time form offers equipment and a rental subtotal")
                status,quote=rental_quote(rental)
                check(status==200 and (quote["grossSatang"],quote["equipmentSatang"],quote["netSatang"])==(53000,13000,53000),"Equipment is charged once per booking alongside two court hours")
                status,quote=rental_quote({**rental,"PromotionId":4})
                check(status==200 and (quote["discountSatang"],quote["netSatang"])==(8000,45000),"Court discount preserves full equipment rental price")
                for invalid in [{"Equipment[racket]":-1},{"Equipment[racket]":11},{"Equipment[racket]":"1.5"},{"Equipment[unknown]":1}]:
                    check(rental_quote({**rental,**invalid})[0]==400,"Invalid rental quantity or item is rejected")
                    check(member.post("/Bookings/Create",{**rental,**invalid})[1]=="/Bookings/Create","Invalid rental cannot be booked")
                check(rental_quote({**rental,"Equipment[racket]":0,"Equipment[shoes]":0})[1]["equipmentSatang"]==0,"Equipment rental is optional")
                _,rental_path,rental_body=member.post("/Bookings/Create",{**rental,"EquipmentSatang":1,"NetSatang":1})
                check("/Bookings/Result/" in rental_path,"Booking with rental succeeds")
                rental_id=int(rental_path.rsplit("/",1)[1])
                rental_snapshot=query("SELECT EquipmentJson,NetSatang FROM Bookings WHERE Id=?",(rental_id,))[0]
                check(rental_snapshot[1]==53000 and len(json.loads(rental_snapshot[0]))==2,"Stored rental ignores submitted prices and preserves both items")
                check("ไม้แบดมินตัน" in html.unescape(rental_body) and "รองเท้ากีฬา" in html.unescape(admin.request(f"/Admin/Booking/{rental_id}")[2]),"Customer and admin details show selected equipment")
                total=query("SELECT COUNT(*) FROM Bookings")[0][0]
                member.post("/Account/Logout",{},token_page="/Bookings/History")
                check(member.request("/Bookings/History")[1]=="/Account/Login","Logout removes member access")
                stop();start()
                member=Browser(base);member.login("test@example.com")
                check(query("SELECT COUNT(*) FROM Bookings")[0][0]==total and member.request(f"/Bookings/Result/{booking_id}")[0]==200,"Saved bookings persist after process restart")
                check(query('SELECT IsActive FROM RentalEquipment WHERE Id=?',(equipment_id,))[0][0]==0,"Equipment remains disabled after restart")
                check(query("SELECT EquipmentJson,NetSatang FROM Bookings WHERE Id=?",(rental_id,))[0]==rental_snapshot and "ไม้แบดมินตัน" in html.unescape(member.request(rental_path)[2]),"Equipment snapshot survives restart")
                check(query("SELECT PaidAt,PaidBy,PaymentReference,NetSatang FROM Bookings WHERE Id=?",(booking_id,))[0]==receipt,"Approval and payment survive restart")
                check(query("SELECT TransferReference,SlipData FROM Bookings WHERE Id=?",(booking_id,))[0]==("TX-510",pending_image),"Payment proof survives restart")
                check(query("SELECT ImageData,ImageContentType FROM Courts WHERE Id=2")[0]==(png,"image/png"),"Uploaded photo survives process restart")
                # Restore the old schema in a temporary copy to exercise upgrades with real existing rows.
                stop()
                legacy=Path(temporary)/"legacy.db"
                with closing(sqlite3.connect(db)) as source, closing(sqlite3.connect(legacy)) as target:
                    source.backup(target)
                db=legacy;environment["Database__Path"]=str(db)
                with closing(sqlite3.connect(db)) as connection:
                    for column in ["ConfirmedAt","ConfirmedBy","PaidAt","PaidBy","PaymentReference","CancelledAt","PaymentSubmittedAt","TransferReference","SlipData","SlipContentType"]:
                        connection.execute(f"ALTER TABLE Bookings DROP COLUMN {column}")
                    connection.execute("ALTER TABLE Courts DROP COLUMN SportTypeId")
                    for column in ["ImageData","ImageContentType","ImageVersion"]:
                        connection.execute(f"ALTER TABLE Courts DROP COLUMN {column}")
                    for table in ['EquipmentMovements','EquipmentLoans','EquipmentStock']:
                        connection.execute('DROP TABLE '+table)
                    connection.execute("DROP TABLE RentalEquipment")
                    connection.execute("DROP TABLE SportTypes")
                    connection.execute("UPDATE Bookings SET Status='Confirmed'")
                    connection.commit()
                legacy_rows=query("SELECT Id,UserId,CourtName,PlayDate,NetSatang,Status FROM Bookings ORDER BY Id")
                legacy_slots=query("SELECT * FROM BookedSlots ORDER BY CourtId,PlayDate,Hour")
                start()
                check(query("SELECT Id,UserId,CourtName,PlayDate,NetSatang,Status FROM Bookings ORDER BY Id")==legacy_rows and query("SELECT * FROM BookedSlots ORDER BY CourtId,PlayDate,Hour")==legacy_slots,"Legacy migration preserves bookings, money, confirmations and reserved slots")
                check(query("SELECT COUNT(*) FROM Courts WHERE SportTypeId=1")[0][0]==3 and query("PRAGMA foreign_key_check")==[],"Legacy courts receive default sport with valid relationships")
                stop();start()
                check(query("SELECT Id,UserId,CourtName,PlayDate,NetSatang,Status FROM Bookings ORDER BY Id")==legacy_rows,"Schema migration can run repeatedly without data loss")
                # Deletion hides courts while preserving all booking and payment records.
                admin=Browser(admin_base);admin.login("admin@courtbook.local","CourtBook123!")
                member=Browser(base);member.login("test@example.com")
                existing_bookings=query("SELECT * FROM Bookings ORDER BY Id")
                existing_slots=query("SELECT * FROM BookedSlots ORDER BY CourtId,PlayDate,Hour")
                check('action="/Admin/DeleteCourt/1"' in admin.request('/Admin/Courts')[2]
                      and 'action="/Admin/DeleteCourt/1"' in admin.request('/Admin/Court/1')[2],"Court list and editor offer deletion forms")
                check(admin.request('/Admin/DeleteCourt/1')[0] in [404,405],"GET cannot delete a court")
                check(admin.request('/Admin/DeleteCourt/1',{})[0]==400,"Court deletion requires CSRF token")
                outsider=Browser(admin_base)
                check(outsider.post('/Admin/DeleteCourt/1',{},token_page='/Account/Login')[1]=='/Account/Login',"Guests cannot delete courts")
                check(member.request('/Admin/DeleteCourt/1',{})[0]==404,"Customer portal cannot delete courts")
                check(admin.post('/Admin/DeleteCourt/999999',{},token_page='/Admin/Courts')[0]==404,"Deleting missing court returns 404")
                for court_id, in query("SELECT Id FROM Courts ORDER BY Id"):
                    check(admin.post(f'/Admin/DeleteCourt/{court_id}',{},token_page='/Admin/Courts')[1]=='/Admin/Courts',"Admin can delete court with existing bookings")
                    check(admin.request(f'/Admin/Court/{court_id}')[0]==404,"Deleted court cannot be edited")
                    check(member.quote(court_id,weekday,[9])[0]==400,"Deleted court cannot receive new bookings")
                    check(member.request(f'/Courts/Image/{court_id}')[0]==404,"Deleted court image is unavailable")
                check(admin.post('/Admin/DeleteCourt/1',{},token_page='/Admin/Court/0')[0]==404,"Repeated deletion returns 404")
                check(query("SELECT * FROM Bookings ORDER BY Id")==existing_bookings
                      and query("SELECT * FROM BookedSlots ORDER BY CourtId,PlayDate,Hour")==existing_slots
                      and query("PRAGMA foreign_key_check")==[],"Deleting courts preserves booking history and valid relationships")
                check('action="/Admin/DeleteCourt/' not in admin.request('/Admin/Courts')[2]
                      and 'class="court-card' not in member.request('/Courts')[2],"Deleted courts disappear from admin and customer lists")
                check(member.request(f'/Bookings/Result/{booking_id}')[0]==200,"Existing booking remains readable after court deletion")
                stop();start()
                check(query("SELECT COUNT(*) FROM Courts WHERE IsDeleted=0")[0][0]==0
                      and query("SELECT * FROM Bookings ORDER BY Id")==existing_bookings,"Deleted courts stay deleted after restart without reseeding")
                print("ALL END-TO-END CHECKS PASSED",flush=True)
            except Exception:
                stop();log.flush();print((Path(temporary)/"server.log").read_text(encoding="utf-8")[-10000:])
                raise
            finally:stop()


if __name__=="__main__":run()
