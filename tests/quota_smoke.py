"""Exercise membership quotas, snapshots and concurrent reservations through MVC."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import calendar
import datetime as dt
import html
import os
import socket
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, DLL, ROOT, TODAY, PASSWORD, check

def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]

def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-quota-') as directory:
        temp=Path(directory);db=temp/'test.db';base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(db),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Demo__AutomaticTopUps':'true','Logging__LogLevel__Default':'Warning'}
        def sql(query,args=()):
            with closing(sqlite3.connect(db)) as c:
                rows=c.execute(query,args).fetchall();c.commit();return rows
        server=None
        with (temp/'server.log').open('w',encoding='utf-8') as log:
            def start():
                nonlocal server
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(100):
                    try:
                        if Browser(base).request('/')[0]==200:return
                    except OSError:pass
                    if server.poll() is not None:raise RuntimeError('Host exited')
                    time.sleep(.1)
                raise RuntimeError('Host timeout')
            def stop():
                if server and server.poll() is None:server.terminate();server.wait(timeout=10)
            try:
                start();admin=Browser(admin_base);admin.login('admin@courtbook.local','CourtBook123!')
                member=Browser(base);annual=Browser(base)
                for b,name in [(member,'monthly'),(annual,'annual')]:
                    b.post('/Account/Register',{'Name':name,'Email':name+'@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD});b.login(name+'@example.com')
                    _,checkout,_=b.post('/Wallet/StartDemoPayment',{'Amount':5000},token_page='/Wallet')
                    b.post('/Wallet/CompleteDemoPayment',{'id':checkout.rsplit('/',1)[1],'outcome':'Paid'},token_page=checkout)
                sport=sql('SELECT SportTypeId FROM Courts WHERE Id=1')[0][0]
                terms={'Id':'monthly','Months':1,'Price':100,'DiscountPercent':10,'MonthlyHours':2,'QuotaSportTypeId':sport,'IsActive':'true'}
                for change in [{'MonthlyHours':101},{'QuotaSportTypeId':999}]:admin.post('/Admin/Plans',{**terms,**change})
                check(sql("SELECT MonthlyHours FROM MembershipPlans WHERE Id='monthly'")[0][0]==0,'Quota configuration rejects excessive hours and unknown sports')
                admin.post('/Admin/Plans',terms)
                purchase={'planId':'monthly','expectedMonths':1,'expectedPriceSatang':10000,'expectedDiscountPercent':10,'expectedMonthlyHours':2,'expectedQuotaSportTypeId':sport}
                member.post('/Wallet/Buy',{**purchase,'expectedMonthlyHours':1},token_page='/Home/Membership')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Purchase rejects stale quota terms')
                member.post('/Wallet/Buy',purchase,token_page='/Home/Membership')
                check(sql('SELECT MonthlyHours,QuotaSportTypeId FROM Memberships')[0]==(2,sport),'Purchased quota and sport are snapshotted')
                receipt=html.unescape(member.request('/Receipts/Details/RC-MB-000001')[2])
                check('2 ชั่วโมงสนาม / เดือน' in receipt,'Membership receipt includes purchased hours')
                date=TODAY+dt.timedelta(days=1);price=sql('SELECT PriceSatang FROM Courts WHERE Id=1')[0][0]
                quote=member.quote(1,date,[8,9])[1]
                check((quote['includedHours'],quote['netSatang'])==(2,0),'Included court hours cover the court charge')
                def book(browser,hours,**extra):
                    return browser.post('/Bookings/Create',{'CourtId':1,'Date':str(date),'Hours':hours,**extra})
                _,path,_=book(member,[8,9]);booking=int(path.rsplit('/',1)[1])
                check(sql('SELECT NetSatang,PaidAt IS NOT NULL FROM Bookings WHERE Id=?',(booking,))[0]==(0,1),'Zero-cash quota booking is settled atomically')
                check(member.request(f'/Receipts/Details/RC-BK-{booking:06d}')[0]==200,'Quota-only booking issues entry evidence while awaiting approval')
                quote=member.quote(1,date,[10])[1]
                check(quote['includedHours']==0 and quote['netSatang']==price*9//10,'Excess hours receive the purchased discount')
                member.post(f'/Bookings/Cancel/{booking}',{},token_page=f'/Bookings/Result/{booking}')
                check(member.quote(1,date,[8,9])[1]['includedHours']==2,'Cancelling zero-cash booking restores quota')
                before_quota=sql('SELECT COUNT(*) FROM MembershipQuotaUses')[0][0]
                before_receipts=sql('SELECT COUNT(*) FROM PaymentReceipts')[0][0]
                sql("CREATE TRIGGER fail_quota_booking BEFORE INSERT ON BookedSlots BEGIN SELECT RAISE(ABORT,'forced slot storage failure'); END")
                check(book(member,[8,9])[0]==500,'Forced slot storage error is surfaced')
                check(sql('SELECT COUNT(*) FROM MembershipQuotaUses')[0][0]==before_quota and sql('SELECT COUNT(*) FROM PaymentReceipts')[0][0]==before_receipts and member.quote(1,date,[8,9])[1]['includedHours']==2,'Failed booking rolls back quota reservation and its receipt')
                sql('DROP TRIGGER fail_quota_booking')
                token=member.token('/Bookings/Create?courtId=1')
                def reserve(pair):
                    b,hours=pair
                    return b.request('/Bookings/Create',{'CourtId':1,'Date':str(date),'Hours':hours,'ExpectedIncludedHours':2,'__RequestVerificationToken':token})
                with ThreadPoolExecutor(max_workers=2) as pool:
                    outcomes=list(pool.map(reserve,[(member.clone(),[8,9]),(member.clone(),[10,11])]))
                check(sum('/Bookings/Result/' in x[1] for x in outcomes)==1,'Concurrent checkout cannot silently replace free hours with a charge')
                used=sql("SELECT SUM(q.Hours) FROM MembershipQuotaUses q JOIN Bookings b ON b.Id=q.BookingId WHERE b.Status<>'Cancelled'")[0][0]
                check(used==2,'Concurrent bookings cannot exceed monthly allowance')
                active=sql("SELECT Id FROM Bookings WHERE Status<>'Cancelled'")[0][0]
                member.post(f'/Bookings/Cancel/{active}',{},token_page=f'/Bookings/Result/{active}')
                equipment=sql('SELECT Id FROM RentalEquipment WHERE SportTypeId=? LIMIT 1',(sport,))[0][0]
                admin.post('/Admin/EquipmentStock/'+equipment,{'TotalQuantity':50,'RepairQuantity':0,'Revision':0,'Note':'Counted quota test inventory'})
                _,path,_=book(member,[12],**{f'Equipment[{equipment}]':1});mixed=int(path.rsplit('/',1)[1])
                amount=sql('SELECT NetSatang FROM Bookings WHERE Id=?',(mixed,))[0][0]
                check(amount>0 and sql('SELECT Hours FROM MembershipQuotaUses WHERE BookingId=?',(mixed,))[0][0]==1,'Equipment is charged separately from included hours')
                member.post(f'/Bookings/PayWallet/{mixed}',{},token_page='/Wallet')
                member.post('/Accounting/RequestRefund',{'BookingId':mixed,'Reason':'Unable to attend session'},token_page='/Accounting')
                admin.post('/Admin/ReviewRefund/1',{'approve':'true'},token_page='/Admin/Refunds')
                check(member.quote(1,date,[8,9])[1]['includedHours']==2,'Approved cash refund restores the associated court quota')
                admin.post('/Admin/Plans',{**terms,'MonthlyHours':5,'DiscountPercent':50})
                check(member.quote(1,date,[8,9,10])[1]['includedHours']==2 and member.quote(1,date,[8,9,10])[1]['netSatang']==price*9//10,'Plan edits preserve purchased quota and overage discount')
                admin.post('/Admin/SportTypes',{'Name':'Tennis'})
                tennis=sql("SELECT Id FROM SportTypes WHERE Name='Tennis'")[0][0]
                admin.post('/Admin/Court/0',{'Id':0,'Name':'Tennis test','Description':'Test court','Location':'Test','Price':500,'SportTypeId':tennis,'IsActive':'true'})
                tennis_court=sql("SELECT Id FROM Courts WHERE Name='Tennis test'")[0][0]
                check(member.quote(tennis_court,date,[8])[1]['discountSatang']==0,'Sport-specific package cannot cover another sport')
                # Promotion wins ties without consuming hours.
                admin.post('/Admin/Promotion/0',{'Id':0,'Name':'Full promotion','Rule':'Threshold','Kind':'Percent','Value':100,'MinSpend':1,'MinHours':1,'MinDays':1,'Starts':str(TODAY),'Ends':str(TODAY+dt.timedelta(days=365)),'IsActive':'true'})
                promo=sql("SELECT Id FROM Promotions WHERE Name='Full promotion'")[0][0]
                check(member.quote(1,date,[8],promo)[1]['includedHours']==0,'Equal-value promotion preserves quota')
                admin.post('/Admin/Plans',{**terms,'Id':'yearly','Months':12,'MonthlyHours':1})
                annual.post('/Wallet/Buy',{**purchase,'planId':'yearly','expectedMonths':12,'expectedMonthlyHours':1},token_page='/Home/Membership')
                membership=sql("SELECT Id,Starts,Ends FROM Memberships WHERE PlanId='yearly'")[0]
                start_date=dt.date.fromisoformat(membership[1]);end_date=dt.date.fromisoformat(membership[2])
                month=start_date.month%12+1;year=start_date.year+(start_date.month//12)
                next_start=dt.date(year,month,min(start_date.day,calendar.monthrange(year,month)[1]))
                _,path,_=book(annual,[14]);annual_booking=int(path.rsplit('/',1)[1])
                check(annual.quote(1,date,[15])[1]['includedHours']==0 and annual.quote(1,next_start,[8])[1]['includedHours']==1,'Annual quota resets for each play-date month without rollover')
                check(annual.quote(1,end_date,[8])[1]['discountSatang']==0,'Membership expiry excludes both quota and discount')
                # A legacy Jan-31 activation preserves the original day anchor after February.
                anchor=dt.date(TODAY.year,1,31)
                if anchor>TODAY:anchor=dt.date(TODAY.year-1,1,31)
                sql('UPDATE Memberships SET Starts=?,Ends=? WHERE Id=?',(str(anchor),str(anchor.replace(year=anchor.year+1)),membership[0]))
                page=html.unescape(annual.request('/Home/Membership')[2])
                check('ชั่วโมงสนามของฉัน' in page and page.count(' / 1 ชม.')==12,'Annual member sees all twelve monthly balances')
                for month in range(1,13):
                    boundary=dt.date(anchor.year,month,calendar.monthrange(anchor.year,month)[1]) if month==2 else dt.date(anchor.year,month,min(31,calendar.monthrange(anchor.year,month)[1]))
                    if TODAY<boundary<anchor.replace(year=anchor.year+1):
                        check(annual.quote(1,boundary,[8])[1]['quotaPeriod']==str(boundary),'Month-end membership keeps original activation day anchor')
                check(sql('PRAGMA foreign_key_check')==[],'Quota relationships remain valid')
                before=sql('SELECT * FROM MembershipQuotaUses');stop();start()
                check(sql('SELECT * FROM MembershipQuotaUses')==before,'Quota migration is repeatable and reservations survive restart')
            except Exception:
                log.flush();print((temp/'server.log').read_text(encoding='utf-8')[-6000:]);raise
            finally:stop()
    print('ALL QUOTA CHECKS PASSED')

if __name__=='__main__':run()
