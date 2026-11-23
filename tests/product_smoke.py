"""Catalogue, account recovery and reporting checks against an isolated real MVC host."""
from contextlib import closing
from pathlib import Path
from email import policy
from email.parser import BytesParser
import datetime as dt
import html
import json
import os
import re
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.parse
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check, fund_and_pay


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-product-') as directory:
        temp=Path(directory); database=temp/'test.db'; pickup=temp/'mail'
        base=f'http://127.0.0.1:{port()}'; admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite',
             'Database__Path':str(database),'Portals__AdminPort':admin_base.rsplit(':',1)[1],
             'Account__PublicOrigin':base,'Account__AdminOrigin':admin_base,
             'Email__Delivery':'Pickup','Email__PickupDirectory':str(pickup),
             'Logging__LogLevel__Default':'Warning'}
        def query(sql,args=()):
            with closing(sqlite3.connect(database)) as c:return c.execute(sql,args).fetchall()
        with (temp/'server.log').open('w',encoding='utf-8') as log:
            server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
            try:
                for _ in range(100):
                    try:
                        if Browser(base).request('/')[0]==200:break
                    except OSError:pass
                    if server.poll() is not None:raise RuntimeError('Host exited')
                    time.sleep(.1)
                admin=Browser(admin_base);admin.login('admin@courtbook.local','CourtBook123!')
                customer=Browser(base)
                customer.post('/Account/Register',{'Name':'Test Player','Email':'product@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                customer.login('product@example.com')
                user_id=query('SELECT Id FROM Users WHERE Email=?',('product@example.com',))[0][0]
                check(all(customer.request(route)[0]==200 for route in ['/Home/About','/Home/Membership','/Courts/Details/1','/Account/EditProfile','/Account/ForgotPassword']), 'New public and account pages render')
                for route in ['/','/Home/Membership','/Wallet']:
                    page=html.unescape(customer.request(route)[2])
                    check(all(tier not in page for tier in ['Basic','Premium','VIP']),'Retired tier names are absent from customer pages')
                check(all(tier not in admin.request('/Admin/Plans')[2] for tier in ['Basic','Premium','VIP']),'Admin only manages monthly and yearly plans')
                for tier in ['basic','premium','vip']:
                    admin.post('/Admin/Plans',{'Id':tier,'Months':1,'Price':99,'DiscountPercent':10,'IsActive':'true'})
                    check(query('SELECT COUNT(*) FROM MembershipPlans WHERE Id=? AND IsActive=1',(tier,))[0][0]==0,'Retired plans cannot be enabled')
                    customer.post('/Wallet/Buy',{'planId':tier,'expectedPriceSatang':9900,'expectedDiscountPercent':10,'expectedMonths':1},token_page='/Wallet')
                    check(query('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Retired plans cannot be purchased')
                home=customer.request('/')[2]
                check(all(f'/images/sports/{sport}.png' in home for sport in ['badminton','football','tennis']), 'Homepage includes all three sport categories')
                for sport in ['badminton','football','tennis']:
                    with customer.opener.open(base+f'/images/sports/{sport}.png') as image:check(image.status==200,'Sport illustration is served')
                check(query("SELECT COUNT(*) FROM MembershipPlans WHERE Id IN ('monthly','yearly')")[0][0]==2,'Monthly and yearly membership plans exist')
                check(admin.post('/Admin/Plans',{'Id':'yearly','Months':12,'Price':499,'DiscountPercent':15,'IsActive':'true'})[1]=='/Admin/Plans','Admin configures tier price duration and benefits')
                check(query("SELECT Months,PriceSatang,DiscountPercent FROM MembershipPlans WHERE Id='yearly'")[0]==(12,49900,15),'Membership terms persist')
                admin.post('/Admin/SportTypes',{'Name':'Football'})
                sport_id=query("SELECT Id FROM SportTypes WHERE Name='Football'")[0][0]
                check(query('SELECT COUNT(*) FROM RentalEquipment WHERE SportTypeId=?',(sport_id,))[0][0]==2,'New football sport receives matching editable rental defaults')
                football={'Id':0,'Name':'Football Arena','Description':'Test venue','Price':500,'SportTypeId':sport_id,'IsActive':'true'}
                check(admin.post('/Admin/Court/0',football)[1]=='/Admin/Courts','Admin creates a court without a location field')
                court_id=query("SELECT Id FROM Courts WHERE Name='Football Arena'")[0][0]
                page=customer.request(f'/Courts?sportTypeId={sport_id}')[2]
                check('Football Arena' in page and '/Courts/Details/1' not in page,'Sport filter restricts the schedule')
                check('name="location"' not in page, 'Schedule omits location filter')
                check('Football Arena' in customer.request(f'/Courts?sportTypeId={sport_id}&location=Unknown')[2], 'Legacy location query does not hide courts')
                check('name="Location"' not in admin.request(f'/Admin/Court/{court_id}')[2], 'Court editor omits location field')
                check('0.0' not in html.unescape(customer.request(f'/Courts/Details/{court_id}')[2]).split('class="help"')[-1],'No synthetic review score is created')
                date=TODAY+dt.timedelta(days=5)
                group={'Id':0,'Name':'Group discount','Rule':'Group','Kind':'Percent','Value':10,'MinHours':1,'MinDays':1,'MinSpend':1,'MinPlayers':4,'Starts':str(TODAY),'Ends':str(TODAY+dt.timedelta(days=365)),'IsActive':'true'}
                admin.post('/Admin/Promotion/0',group)
                promotion_id=query("SELECT Id FROM Promotions WHERE Name='Group discount'")[0][0]
                fields={'CourtId':court_id,'Date':str(date),'Hours':[8,9],'PromotionId':promotion_id,'Players':3}
                def quote(values):
                    status,_,body=customer.request('/Bookings/Quote?'+urllib.parse.urlencode(values,doseq=True));return status,json.loads(body)
                check(quote(fields)[0]==400,'Group promotion rejects too few players')
                equipment_id=query('SELECT Id FROM RentalEquipment WHERE SportTypeId=? AND Name=?',(sport_id,'ลูกฟุตบอล'))[0][0]
                admin.post('/Admin/EquipmentStock/'+equipment_id,{'TotalQuantity':50,'RepairQuantity':0,'Revision':0,'Note':'Counted football test inventory'})
                fields={**fields,'Players':4,f'Equipment[{equipment_id}]':1}
                status,data=quote(fields)
                check(status==200 and (data['grossSatang'],data['discountSatang'],data['netSatang'])==(105000,10000,95000),'Group discount applies only to court cost and adds equipment')
                _,booking_path,_=customer.post('/Bookings/Create',fields);booking_id=int(booking_path.rsplit('/',1)[1])
                check(query('SELECT SportTypeName,Players FROM Bookings WHERE Id=?',(booking_id,))[0]==('Football',4),'Booking records sport and player count')
                damage_path=f'/Bookings/ReportDamage/{booking_id}'
                check('ReportDamage' in customer.request(booking_path)[2] and customer.request(damage_path)[0]==200,'Customer can open reporting from booking details')
                check(customer.request(damage_path,{})[0]==400,'Damage reporting requires CSRF')
                check(customer.request('/Admin/DamageReports')[0]==404 and admin.request('/Admin/DamageReports')[0]==200,'Damage management is restricted to admin portal')
                other=Browser(base)
                other.post('/Account/Register',{'Name':'Other Player','Email':'damage-other@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                other.login('damage-other@example.com')
                check(other.request(damage_path+'?userId='+str(user_id))[0]==404,'Another account cannot read a booking report page')
                check(other.post(damage_path,{'Category':'Court','Description':'Forged damage'},token_page='/Bookings/History')[0]==404,'Another account cannot report for someone else booking')
                for bad in [{'Category':'Unknown','Description':'Damaged court'},{'Category':'Equipment','EquipmentId':'racket','Description':'Damaged item'},{'Category':'Court','Description':'x'},{'Category':'Court','Description':'x'*2001}]:
                    customer.post(damage_path,bad,token_page=damage_path)
                    check(query('SELECT COUNT(*) FROM DamageReports')[0][0]==0,'Invalid category, non-rented equipment or description is rejected')
                court_issue={'Category':'Court','Description':'Surface damaged near the goal','Status':'Resolved','AdminNote':'forged note'}
                customer.post(damage_path,court_issue,token_page=damage_path)
                equipment_issue={'Category':'Equipment','EquipmentId':equipment_id,'Description':'Football valve is damaged'}
                customer.post(damage_path,equipment_issue,token_page=damage_path)
                report_ids=[row[0] for row in query('SELECT Id FROM DamageReports ORDER BY Id')]
                check(len(report_ids)==2 and query('SELECT Status,AdminNote FROM DamageReports ORDER BY Id')==[('Reported',''),('Reported','')],'Customer can report court and rented equipment without forging admin state')
                check(query('SELECT EquipmentName FROM DamageReports WHERE Id=?',(report_ids[1],))[0][0]=='ลูกฟุตบอล','Reported equipment uses the booking snapshot name')
                update=f'/Admin/UpdateDamageReport/{report_ids[0]}'
                check(admin.request(update,{})[0]==400,'Admin report update requires CSRF')
                check(customer.post(update,{'Status':'Resolved'},token_page=damage_path)[0]==404,'Customer cannot update report status')
                admin.post(update,{'Status':'Invalid','AdminNote':'bad'},token_page='/Admin/DamageReports')
                check(query('SELECT Status FROM DamageReports WHERE Id=?',(report_ids[0],))[0][0]=='Reported','Invalid admin status is rejected')
                admin.post(update,{'Status':'InProgress','AdminNote':'Maintenance team scheduled'},token_page='/Admin/DamageReports')
                check('Maintenance team scheduled' in customer.request(damage_path)[2],'Customer sees admin reply on own reports')
                filtered=admin.request('/Admin/DamageReports?status=InProgress')[2]
                check('Surface damaged near the goal' in filtered and 'Football valve is damaged' not in filtered,'Admin can filter reports by status')
                admin.post(update,{'Status':'Resolved','AdminNote':'Surface repaired'},token_page='/Admin/DamageReports')
                check(query('SELECT Status,UpdatedBy FROM DamageReports WHERE Id=?',(report_ids[0],))[0]==('Resolved',1),'Admin resolution records status and responsible admin')
                check('Surface repaired' in customer.request(damage_path)[2],'Customer can follow the resolved report')
                direct='/Bookings/ReportDamage'
                check(customer.request(direct)[0]==200 and 'general-report-form' in customer.request(direct)[2],'Standalone report form works without a booking id')
                error_status,_,error_page=customer.request('/Home/Error')
                check(error_status==500 and 'general-report-form' in error_page and 'action="/Bookings/ReportDamage"' in error_page,'Error page contains a working direct message form')
                for category,subject in [('Other','Website issue'),('Court','Training field'),('Equipment','Shared training cone')]:
                    customer.post(direct,{'Category':category,'Subject':subject,'Description':'Direct report about '+subject,'BookingId':999999,'UserId':999999},token_page=direct)
                check(query('SELECT COUNT(*) FROM DamageReports WHERE BookingId IS NULL AND UserId=?',(user_id,))[0][0]==3,'General court and equipment reports save with actual reporter and no booking')
                other.post(direct,{'Category':'Other','Subject':'No booking needed','Description':'Message from other customer','UserId':user_id},token_page=direct)
                check('Message from other customer' not in customer.request(direct)[2] and 'Surface repaired' not in other.request(direct)[2],'Direct reports remain private to their actual sender')
                direct_id=query("SELECT Id FROM DamageReports WHERE Subject='Website issue'")[0][0]
                admin.post(f'/Admin/UpdateDamageReport/{direct_id}',{'Status':'Resolved','AdminNote':'Direct issue fixed'},token_page='/Admin/DamageReports')
                check('Direct issue fixed' in customer.request(direct)[2],'Admin can reply to a report with no booking')

                check(admin.request('/Admin/Revenue')[0]==200 and customer.request('/Admin/Revenue')[0]==404,'Revenue report is available only on admin portal')
                review={'CourtId':court_id,'Rating':5,'Comment':'Great court'}
                customer.post('/Courts/Review',review,token_page=f'/Courts/Details/{court_id}')
                check(query('SELECT COUNT(*) FROM CourtReviews')[0][0]==0,'Review cannot be forged before playing')
                booking_receipt=f'/Receipts/Details/RC-BK-{booking_id:06d}'
                check(customer.request(booking_receipt)[0]==404,'Unpaid booking has no receipt')
                admin.post(f'/Admin/ConfirmBooking/{booking_id}',{},token_page=f'/Admin/Booking/{booking_id}')
                fund_and_pay(customer,booking_id)
                check(customer.request(booking_receipt)[0]==200 and 'RC-BK-' in customer.request(booking_path)[2],'Settled booking automatically exposes its receipt')
                check(other.request(booking_receipt+'?userId='+str(user_id))[0]==404,'Other customers cannot access a receipt with a forged owner query')
                receipt_id=f'RC-BK-{booking_id:06d}'
                receipt_snapshot=query('SELECT SnapshotJson FROM PaymentReceipts WHERE Id=?',(receipt_id,))[0][0]
                receipt_data=json.loads(receipt_snapshot)
                check(receipt_data['NetSatang']==95000 and len(receipt_data['Lines'])==2,'Receipt snapshots exact court equipment discount and net amounts')
                admin.post(f'/Admin/ConfirmPayment/{booking_id}',{'Reference':'REPEAT'},token_page=f'/Admin/Booking/{booking_id}')
                check(query('SELECT COUNT(*) FROM PaymentReceipts WHERE BookingId=?',(booking_id,))[0][0]==1 and query('SELECT SnapshotJson FROM PaymentReceipts WHERE Id=?',(receipt_id,))[0][0]==receipt_snapshot,'Repeated payment confirmation cannot duplicate or rewrite a receipt')
                check(admin.request(f'/Admin/Receipt/{receipt_id}')[0]==200,'Staff can verify a receipt through the admin portal')
                report=html.unescape(admin.request(f'/Admin/Revenue?from={date}&to={date}')[2])
                check('950.00' in report,'Revenue report uses actual saved paid total')
                with closing(sqlite3.connect(database)) as c:
                    c.execute('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY-dt.timedelta(days=1)),booking_id));c.commit()
                customer.post('/Courts/Review',review,token_page=f'/Courts/Details/{court_id}')
                customer.post('/Courts/Review',{**review,'Rating':4},token_page=f'/Courts/Details/{court_id}')
                check(query('SELECT Rating FROM CourtReviews')[0][0]==4 and len(query('SELECT * FROM CourtReviews'))==1,'Completed booking can create and update one real review')
                check('4.0' in html.unescape(customer.request(f'/Courts/Details/{court_id}')[2]),'Real review aggregate appears on details')
                _,checkout,_=customer.post('/Wallet/StartDemoPayment',{'Amount':1000},token_page='/Wallet')
                payment_id=checkout.rsplit('/',1)[1]
                customer.post('/Wallet/CompleteDemoPayment',{'id':payment_id,'outcome':'Paid'},token_page=checkout)
                purchase={'planId':'yearly','expectedPriceSatang':49900,'expectedDiscountPercent':15,'expectedMonths':1}
                customer.post('/Wallet/Buy',purchase,token_page='/Wallet')
                check(query('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Stale membership duration cannot be purchased')
                customer.post('/Wallet/Buy',{**purchase,'expectedMonths':12},token_page='/Wallet')
                check(query('SELECT PlanId FROM Memberships')[0][0]=='yearly','Yearly plan can be bought with explicitly accepted terms')
                membership_id=query('SELECT Id FROM Memberships WHERE UserId=?',(user_id,))[0][0]
                membership_receipt=f'/Receipts/Details/RC-MB-{membership_id:06d}'
                check(customer.request(membership_receipt)[0]==200 and other.request(membership_receipt)[0]==404,'Purchased membership has a private receipt')
                membership_snapshot=query('SELECT SnapshotJson FROM PaymentReceipts WHERE MembershipId=?',(membership_id,))[0][0]
                admin.post('/Admin/Plans',{'Id':'yearly','Months':12,'Price':799,'DiscountPercent':30,'IsActive':'false'})
                check(query('SELECT SnapshotJson FROM PaymentReceipts WHERE MembershipId=?',(membership_id,))[0][0]==membership_snapshot and json.loads(membership_snapshot)['DiscountPercent']==15,'Plan edits preserve the purchased receipt price and benefits')
                # A receipt write failure must roll back the payment and ledger together.
                _,wallet_booking,_=customer.post('/Bookings/Create',{'CourtId':1,'Date':str(TODAY+dt.timedelta(days=20)),'Hours':[19]})
                wallet_booking_id=int(wallet_booking.rsplit('/',1)[1])
                before_balance=query('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user_id,))[0][0]
                with closing(sqlite3.connect(database)) as c:
                    c.execute("CREATE TRIGGER fail_receipt BEFORE INSERT ON PaymentReceipts BEGIN SELECT RAISE(ABORT,'simulated receipt write failure'); END;")
                response=customer.post(f'/Bookings/PayWallet/{wallet_booking_id}',{},token_page=wallet_booking)
                check(response[0]==500 and query('SELECT PaidAt FROM Bookings WHERE Id=?',(wallet_booking_id,))[0][0] is None and query('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user_id,))[0][0]==before_balance,'Receipt storage failure rolls back wallet debit and payment status')
                with closing(sqlite3.connect(database)) as c:c.execute('DROP TRIGGER fail_receipt')
                customer.post(f'/Bookings/PayWallet/{wallet_booking_id}',{},token_page=wallet_booking)
                wallet_receipt=f'/Receipts/Details/RC-BK-{wallet_booking_id:06d}'
                wallet_body=html.unescape(customer.request(wallet_receipt)[2])
                check('รอผู้ดูแลยืนยันการจอง' in wallet_body,'Paid pending booking receipt does not claim confirmed entry')
                admin.post(f'/Admin/ConfirmBooking/{wallet_booking_id}',{},token_page=f'/Admin/Booking/{wallet_booking_id}')
                check('ชำระเงินและยืนยันการจองแล้ว' in html.unescape(customer.request(wallet_receipt)[2]),'Entry status follows current booking approval')

                existing=customer.clone()
                check(customer.post('/Account/EditProfile',{'Name':'Updated Player','Email':'changed@example.com','CurrentPassword':'wrong'})[1]=='/Account/EditProfile','Changing email requires current password')
                check(customer.post('/Account/EditProfile',{'Name':'Updated Player','Email':'changed@example.com','CurrentPassword':PASSWORD})[1]=='/Account/Login','Profile change saves and requests sign-in')
                check(existing.request('/Bookings/History')[1]=='/Account/Login','Email change invalidates previous sessions')
                check(query('SELECT SnapshotJson FROM PaymentReceipts WHERE Id=?',(receipt_id,))[0][0]==receipt_snapshot,'Profile edits do not rewrite the receipt customer identity')
                customer.login('changed@example.com')
                old_session=customer.clone()
                customer.post('/Account/ForgotPassword',{'Email':'missing@example.com'})
                check(not list(pickup.glob('*.eml')),'Unknown email receives no reset link')
                customer.post('/Account/ForgotPassword',{'Email':'changed@example.com'})
                messages=list(pickup.glob('*.eml'));check(len(messages)==1,'Reset email is written only to isolated local test pickup')
                message=BytesParser(policy=policy.default).parsebytes(messages[0].read_bytes())
                body=message.get_body(preferencelist=('plain',)).get_content() if message.is_multipart() else message.get_content()
                link=re.search(r'http://[^\s<>]+/Account/ResetPassword\?token=[^\s<>]+',body).group(0)
                route=urllib.parse.urlsplit(link).path+'?'+urllib.parse.urlsplit(link).query
                token=urllib.parse.parse_qs(urllib.parse.urlsplit(link).query)['token'][0]
                check(customer.request(route)[0]==200,'Valid reset link opens a reset form')
                check(admin.request('/Account/ResetPassword?token='+urllib.parse.quote(token))[2].find('name="Password"')<0,'Customer token cannot reset through the admin portal')
                new_password='Updated-password-123!'
                check(customer.post('/Account/ResetPassword',{'Token':token,'Password':new_password,'ConfirmPassword':new_password},token_page=route)[1]=='/Account/Login','Valid token resets password')
                check('name="Password"' not in customer.request(route)[2],'Reset token cannot be reused')
                check(old_session.request('/Bookings/History')[1]=='/Account/Login','Password reset invalidates prior sessions')
                check(customer.login('changed@example.com',PASSWORD)[1]=='/Account/Login' and customer.login('changed@example.com',new_password)[1]=='/Courts','Only the new password authenticates')
                admin.post(f'/Admin/LockCustomer/{user_id}',{'locked':'true'},token_page=f'/Admin/Customer/{user_id}')
                check(customer.request('/Bookings/History')[1]=='/Account/Login' and customer.login('changed@example.com',new_password)[1]=='/Account/Login','Admin suspension ends existing sessions and blocks sign-in')
                admin.post(f'/Admin/LockCustomer/{user_id}',{'locked':'false'},token_page=f'/Admin/Customer/{user_id}')
                server.terminate();server.wait(timeout=10)
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(100):
                    try:
                        if Browser(base).request('/')[0]==200:break
                    except OSError:pass
                    time.sleep(.1)
                customer=Browser(base);customer.login('changed@example.com',new_password)
                check('Surface repaired' in customer.request(damage_path)[2] and query('SELECT COUNT(*) FROM DamageReports')[0][0]==6 and 'Direct issue fixed' in customer.request('/Bookings/ReportDamage')[2],'Damage reports and direct replies survive server restart')
                check(customer.request(booking_receipt)[0]==200 and customer.request(membership_receipt)[0]==200 and query('SELECT SnapshotJson FROM PaymentReceipts WHERE Id=?',(receipt_id,))[0][0]==receipt_snapshot,'Receipts and immutable snapshots survive restart')
                check(query('PRAGMA foreign_key_check')==[],'Expanded schema retains valid relationships')
                # Recreate the previous version in a separate copy, preserving every legacy report field.
                server.terminate();server.wait(timeout=10)
                legacy=temp/'legacy-reports.db'
                with closing(sqlite3.connect(database)) as source,closing(sqlite3.connect(legacy)) as target:
                    source.backup(target)
                columns='Id,BookingId,Category,EquipmentId,EquipmentName,Description,Status,CreatedAt,UpdatedAt,UpdatedBy,AdminNote'
                with closing(sqlite3.connect(legacy)) as c:
                    expected=c.execute(f'SELECT {columns} FROM DamageReports WHERE BookingId IS NOT NULL ORDER BY Id').fetchall()
                    c.executescript("CREATE TABLE LegacyReports(Id INTEGER PRIMARY KEY,BookingId BIGINT NOT NULL REFERENCES Bookings(Id),Category TEXT NOT NULL CHECK(Category IN ('Court','Equipment')),EquipmentId TEXT,EquipmentName TEXT NOT NULL DEFAULT '',Description TEXT NOT NULL,Status TEXT NOT NULL DEFAULT 'Reported',CreatedAt TEXT NOT NULL,UpdatedAt TEXT,UpdatedBy BIGINT REFERENCES Users(Id),AdminNote TEXT NOT NULL DEFAULT '');")
                    c.execute(f'INSERT INTO LegacyReports({columns}) SELECT {columns} FROM DamageReports WHERE BookingId IS NOT NULL')
                    c.executescript('DROP TABLE DamageReports; ALTER TABLE LegacyReports RENAME TO DamageReports;')
                    c.execute('DROP TABLE PaymentReceipts')
                    c.commit()
                env['Database__Path']=str(legacy)
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(100):
                    try:
                        if Browser(base).request('/')[0]==200:break
                    except OSError:pass
                    time.sleep(.1)
                with closing(sqlite3.connect(legacy)) as c:
                    check(c.execute(f'SELECT {columns} FROM DamageReports ORDER BY Id').fetchall()==expected,'Legacy migration preserves all report ids descriptions replies statuses and audit fields')
                    check(c.execute('SELECT COUNT(*) FROM DamageReports WHERE UserId IS NULL').fetchone()[0]==0 and c.execute('PRAGMA foreign_key_check').fetchall()==[],'Legacy reports acquire correct owners and valid relationships')
                customer=Browser(base);customer.login('changed@example.com',new_password)
                customer.post('/Bookings/ReportDamage',{'Category':'Equipment','Subject':'New direct item','Description':'Direct report after legacy upgrade'},token_page='/Bookings/ReportDamage')
                check('Direct report after legacy upgrade' in customer.request('/Bookings/ReportDamage')[2],'Direct reports work after migration from booking-only schema')
                check(customer.request(booking_receipt)[0]==200 and customer.request(membership_receipt)[0]==200,'Historical settled transactions receive receipts when upgrading')
                print('ALL PRODUCT CHECKS PASSED',flush=True)
            except Exception:
                log.flush();print((temp/'server.log').read_text(encoding='utf-8')[-6000:]);raise
            finally:
                server.terminate()
                try:server.wait(timeout=10)
                except subprocess.TimeoutExpired:server.kill();server.wait()


if __name__=='__main__':run()
