"""Exercise real wallet HTTP requests against a disposable database. Build first.
Set COURTBOOK_TEST_DLL to select the tested application build.
"""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import base64
import datetime as dt
import os
import socket
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, DLL, PASSWORD, ROOT, TODAY, check


def run():
    with tempfile.TemporaryDirectory(prefix="courtbook-wallet-") as temp:
        db = Path(temp)/"wallet.db"
        def port():
            with socket.socket() as s:
                s.bind(('127.0.0.1',0)); return s.getsockname()[1]
        customer_port, admin_port = port(), port()
        base, admin_base = f'http://127.0.0.1:{customer_port}', f'http://127.0.0.1:{admin_port}'
        env = {**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(db),
               'Portals__AdminPort':str(admin_port),'Logging__LogLevel__Default':'Warning','Demo__AutomaticTopUps':'true'}
        server = None
        def sql(query, args=()):
            with closing(sqlite3.connect(db)) as c:
                rows = c.execute(query,args).fetchall(); c.commit(); return rows
        def start():
            nonlocal server
            server = subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
            for _ in range(100):
                if server.poll() is not None: raise RuntimeError('Server exited')
                try:
                    if Browser(base).request('/')[0]==200: return
                except OSError: pass
                time.sleep(.1)
            raise RuntimeError('Server did not start')
        def stop():
            if server and server.poll() is None:
                server.terminate(); server.wait(timeout=10)
        with (Path(temp)/'server.log').open('w',encoding='utf-8') as log:
            try:
                start()
                member = Browser(base)
                member.post('/Account/Register',{'Name':'Wallet member','Email':'wallet@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                member.login('wallet@example.com')
                other = Browser(base)
                other.post('/Account/Register',{'Name':'Other member','Email':'other@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                other.login('other@example.com')
                admin = Browser(admin_base); admin.login('admin@courtbook.local','CourtBook123!')
                check(member.request('/Wallet')[0]==200 and admin.request('/Admin/Plans')[0]==200 and admin.request('/Admin/TopUps')[0]==200,'Wallet, plans and top-up review pages render')
                check(Browser(base).request('/Wallet')[1]=='/Account/Login','Wallet requires login')
                check(member.request('/Admin/TopUps')[0]==404 and admin.request('/Wallet')[0]==404,'Portal restrictions protect wallet administration')
                check(sql('SELECT COUNT(*) FROM MembershipPlans WHERE IsActive=1')[0][0]==0,'Plans start disabled until administrator sets prices')
                check(member.request('/Wallet/Buy',{'planId':'monthly'})[0]==400,'Wallet purchase requires CSRF')
                admin.post('/Admin/Plans',{'Id':'monthly','Price':'100','DiscountPercent':10,'IsActive':'true'})
                admin.post('/Admin/Plans',{'Id':'yearly','Price':'1000','DiscountPercent':20,'IsActive':'true'})
                check(sql('SELECT PriceSatang,DiscountPercent FROM MembershipPlans WHERE Id="monthly"')[0]==(10000,10),'Admin configures monthly price and discount')
                member.post('/Wallet/Buy',{'planId':'monthly','expectedPriceSatang':1,'expectedDiscountPercent':99},token_page='/Wallet')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Purchase rejects stale or tampered plan terms')
                member.post('/Wallet/Buy',{'planId':'monthly','expectedPriceSatang':10000,'expectedDiscountPercent':10},token_page='/Wallet')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Insufficient funds roll back membership creation')
                png = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
                def topup(browser, amount, ref, image):
                    return browser.upload('/Wallet/TopUp',{'Amount':amount,'TransferReference':ref},image,file_field='Slip',token_page='/Wallet')
                for amount in ['0','-1','100000.01','1.001']:
                    topup(member,amount,'invalid-'+amount,png)
                check(sql('SELECT COUNT(*) FROM TopUps')[0][0]==0,'Invalid amounts never create top-ups')
                topup(member,'500','WALLET-001',png)
                check(sql('SELECT COUNT(*) FROM TopUps')[0][0]==1 and sql('SELECT COUNT(*) FROM WalletEntries')[0][0]==0,'Sending proof creates pending request without crediting money')
                with admin.opener.open(admin.base+'/Admin/TopUpSlip/1') as response:
                    check(other.request('/Wallet/Slip/1')[0]==404 and response.read()==png,'Slip is private to owner and administrator')
                topup(other,'500','wallet-001',png+b'2')
                topup(other,'500','WALLET-002',png)
                check(sql('SELECT COUNT(*) FROM TopUps')[0][0]==1,'Duplicate transfer references and slip bytes cannot be reused across users')
                token = admin.token('/Admin/TopUps')
                with ThreadPoolExecutor(max_workers=2) as pool:
                    list(pool.map(lambda b:b.request('/Admin/ReviewTopUp/1',{'approve':'true','__RequestVerificationToken':token}),[admin.clone(),admin.clone()]))
                check(sql('SELECT SUM(AmountSatang),COUNT(*) FROM WalletEntries')[0]==(50000,1),'Concurrent approval credits exactly once')
                topup(member,'50','REJECT-1',png+b'rejected')
                admin.post('/Admin/ReviewTopUp/2',{'approve':'false','note':'Wrong transfer amount'},token_page='/Admin/TopUps')
                admin.post('/Admin/ReviewTopUp/2',{'approve':'true'},token_page='/Admin/TopUps')
                check(sql('SELECT Status FROM TopUps WHERE Id=2')[0][0]=='Rejected' and sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]==50000,'Rejected top-ups add no money and cannot be approved again')
                member.post('/Wallet/Buy',{'planId':'monthly','expectedPriceSatang':10000,'expectedDiscountPercent':10},token_page='/Wallet')
                check(sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]==40000 and sql('SELECT COUNT(*) FROM Memberships')[0][0]==1,'Buying monthly plan debits balance and activates membership')
                starts,ends = sql('SELECT Starts,Ends FROM Memberships')[0]
                check(starts==str(TODAY) and dt.date.fromisoformat(ends)>TODAY,'Membership uses calendar dates and an exclusive expiry')
                member.post('/Wallet/Buy',{'planId':'yearly','expectedPriceSatang':100000,'expectedDiscountPercent':20},token_page='/Wallet')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==1,'Active membership cannot be purchased twice or replaced')
                date = TODAY+dt.timedelta(days=1)
                gross = sql('SELECT PriceSatang FROM Courts WHERE Id=1')[0][0]
                quote = member.quote(1,date,[8])[1]
                check(quote['discountSatang']==gross//10 and quote['membershipName'] is not None,'Membership discount applies automatically')
                expiry = dt.date.fromisoformat(ends)
                check(member.quote(1,expiry,[8])[1]['discountSatang']==0,'Play date on or after expiry receives no membership discount')
                admin.post('/Admin/Plans',{'Id':'monthly','Price':'200','DiscountPercent':50,'IsActive':'false'})
                check(member.quote(1,date,[8])[1]['discountSatang']==gross//10,'Existing member keeps purchased benefits after plan edits')
                discounted = member.quote(1,date,[8],1)[1]
                check(discounted['discountSatang']==max(5000,gross//10),'Promotion and membership use the larger discount without stacking')
                def book(browser,hour):
                    response=browser.post('/Bookings/Create',{'CourtId':1,'Date':str(date),'Hours':[hour]})
                    assert '/Bookings/Result/' in response[1],response
                    return int(response[1].split('/')[-1])
                booking1,booking2 = book(member,8),book(member,9)
                check(other.post(f'/Bookings/PayWallet/{booking1}',{},token_page='/Wallet')[0]==404,'Members cannot spend on another account booking')
                # Leave just enough money for one of two simultaneous payments.
                due = sql('SELECT NetSatang FROM Bookings WHERE Id=?',(booking1,))[0][0]
                balance = sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]
                sql('INSERT INTO WalletEntries(UserId,AmountSatang,Description,CreatedAt) SELECT Id,?,"test balance setup",? FROM Users WHERE Email="wallet@example.com"',(due-balance,dt.datetime.now(dt.timezone.utc).isoformat()))
                token=member.token('/Wallet')
                with ThreadPoolExecutor(max_workers=2) as pool:
                    list(pool.map(lambda pair:pair[0].request(f'/Bookings/PayWallet/{pair[1]}',{'__RequestVerificationToken':token}),[(member.clone(),booking1),(member.clone(),booking2)]))
                check(sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]==0 and sql('SELECT COUNT(*) FROM Bookings WHERE PaidAt IS NOT NULL')[0][0]==1,'Concurrent purchases cannot overdraw wallet or mark unpaid booking paid')
                paid = sql('SELECT Id FROM Bookings WHERE PaidAt IS NOT NULL')[0][0]
                check(member.post(f'/Bookings/PayWallet/{paid}',{},token_page='/Wallet')[0]==404,'Repeated wallet payment does not debit twice')
                check(sql('SELECT COUNT(*) FROM WalletEntries WHERE BookingId IS NOT NULL')[0][0]==1,'Exactly one booking debit is recorded')
                # A booking slip cannot be submitted as a top-up, and vice versa.
                unpaid = booking2 if paid==booking1 else booking1
                response=member.upload(f'/Bookings/SubmitPayment/{unpaid}',{'TransferReference':'BOOKING-X'},png+b'booking',file_field='Slip',token_page=f'/Bookings/Result/{unpaid}')
                assert response[0]==200
                topup(member,'100','BOOKING-X',png+b'another')
                topup(member,'100','OTHER-REF',png+b'booking')
                check(sql('SELECT COUNT(*) FROM TopUps')[0][0]==2,'Booking references and slips cannot become top-up credit')
                member.upload(f'/Bookings/SubmitPayment/{unpaid}',{'TransferReference':'WALLET-001'},png,file_field='Slip',token_page=f'/Bookings/Result/{unpaid}')
                check(sql('SELECT TransferReference FROM Bookings WHERE Id=?',(unpaid,))[0][0]=='BOOKING-X','Top-up proof cannot replace booking payment proof')
                check(sql('PRAGMA foreign_key_check')==[],'Wallet relationships remain valid')
                # Yearly calendar duration and persisted balances on restart.
                topup(other,'1000','YEARLY-001',png+b'yearly')
                yearly_topup=sql('SELECT MAX(Id) FROM TopUps')[0][0]
                admin.post(f'/Admin/ReviewTopUp/{yearly_topup}',{'approve':'true'},token_page='/Admin/TopUps')
                other.post('/Wallet/Buy',{'planId':'yearly','expectedPriceSatang':100000,'expectedDiscountPercent':20},token_page='/Wallet')
                year_start,year_end=sql('SELECT Starts,Ends FROM Memberships WHERE PlanId="yearly"')[0]
                check(dt.date.fromisoformat(year_end).year==dt.date.fromisoformat(year_start).year+1,'Yearly membership lasts twelve calendar months')
                rows = sql('SELECT * FROM WalletEntries ORDER BY Id')
                stop(); start()
                member=Browser(base);member.login('wallet@example.com')
                check(sql('SELECT * FROM WalletEntries ORDER BY Id')==rows and member.request('/Wallet')[0]==200,'Balances, transactions and memberships survive restart')
                # Existing wallets upgrade without changing their balances or history.
                stop(); sql('DROP TABLE DemoPayments'); start()
                check(sql('SELECT * FROM WalletEntries ORDER BY Id')==rows,'Demo payment schema upgrade preserves the existing wallet ledger')
                demo = Browser(base)
                demo.post('/Account/Register',{'Name':'Demo payer','Email':'demo@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                demo.login('demo@example.com')
                other=Browser(base); other.login('other@example.com')
                check('StartDemoPayment' in demo.request('/Wallet')[2],'Wallet offers automatic demo payments')
                check(demo.request('/Wallet/DemoPayment')[0]==400,'Demo checkout requires a payment identifier')
                for value in ['0','-1','100000.01','1.001','not-a-number']:
                    demo.post('/Wallet/StartDemoPayment',{'Amount':value},token_page='/Wallet')
                check(sql('SELECT COUNT(*) FROM DemoPayments')[0][0]==0,'Invalid demo amounts never create a payment')
                check(demo.request('/Wallet/StartDemoPayment',{'Amount':'100'})[0]==400,'Starting a demo payment requires CSRF')
                def begin_demo(value):
                    status,path,body=demo.post('/Wallet/StartDemoPayment',{'Amount':value},token_page='/Wallet')
                    check(status==200 and path.startswith('/Wallet/DemoPayment/'),'Demo checkout opens successfully')
                    return path.rsplit('/',1)[1],path
                def finish_demo(payment_id, outcome, **extra):
                    return demo.post('/Wallet/CompleteDemoPayment/'+payment_id,{'outcome':outcome,**extra},token_page='/Wallet')
                payment_id,checkout=begin_demo('500.25')
                check(sql('SELECT AmountSatang,Status FROM DemoPayments WHERE Id=?',(payment_id,))[0]==(50025,'Pending'),'Server records exact satang before demo payment')
                check('500.25' in demo.request(checkout)[2] and 'name="outcome"' in demo.request(checkout)[2],'Demo checkout displays stored amount and payment outcomes')
                check(other.request(checkout)[0]==404 and other.post('/Wallet/CompleteDemoPayment/'+payment_id,{'outcome':'Paid'},token_page='/Wallet')[0]==404,'Another customer cannot view or complete a demo payment')
                check(demo.request('/Wallet/CompleteDemoPayment/'+payment_id,{'outcome':'Paid'})[0]==400,'Completing a demo payment requires CSRF')
                finish_demo(payment_id,'unknown')
                check(sql('SELECT Status FROM DemoPayments WHERE Id=?',(payment_id,))[0][0]=='Pending','Invalid demo outcome does not settle a payment')
                token=demo.token(checkout)
                with ThreadPoolExecutor(max_workers=2) as pool:
                    list(pool.map(lambda browser:browser.request('/Wallet/CompleteDemoPayment/'+payment_id,{'outcome':'Paid','Amount':'99999','__RequestVerificationToken':token}),[demo.clone(),demo.clone()]))
                demo_user=sql('SELECT Id FROM Users WHERE Email=?',('demo@example.com',))[0][0]
                def demo_balance(): return sql('SELECT COALESCE(SUM(AmountSatang),0),COUNT(*) FROM WalletEntries WHERE UserId=?',(demo_user,))[0]
                check(demo_balance()==(50025,1),'Concurrent demo success credits exactly once using the stored amount')
                check('DEMO-' in sql('SELECT Description FROM WalletEntries WHERE UserId=?',(demo_user,))[0][0],'Demo ledger entry is clearly labelled and traceable')
                finish_demo(payment_id,'Paid')
                check(demo_balance()==(50025,1),'Replaying a successful demo payment never duplicates credit')
                for outcome in ['Failed','Cancelled']:
                    failed_id,_=begin_demo('25')
                    finish_demo(failed_id,outcome); finish_demo(failed_id,'Paid')
                    check(sql('SELECT Status FROM DemoPayments WHERE Id=?',(failed_id,))[0][0]==outcome and demo_balance()==(50025,1),'Failed/cancelled payment cannot later be credited')
                expired_id,expired_path=begin_demo('25')
                sql('UPDATE DemoPayments SET CreatedAt=? WHERE Id=?',((dt.datetime.now(dt.timezone.utc)-dt.timedelta(minutes=16)).isoformat(),expired_id))
                finish_demo(expired_id,'Paid')
                check('name="outcome"' not in demo.request(expired_path)[2] and demo_balance()==(50025,1),'Expired checkout hides payment buttons and rejects credit')
                atomic_id,_=begin_demo('10')
                sql("CREATE TRIGGER fail_demo_credit BEFORE INSERT ON WalletEntries WHEN NEW.UserId="+str(demo_user)+" BEGIN SELECT RAISE(ABORT,'simulated storage failure'); END")
                check(finish_demo(atomic_id,'Paid')[0]==500,'Storage failure is visible during demo settlement')
                check(sql('SELECT Status FROM DemoPayments WHERE Id=?',(atomic_id,))[0][0]=='Pending' and demo_balance()==(50025,1),'Storage failure rolls back both payment state and wallet credit')
                sql('DROP TRIGGER fail_demo_credit'); finish_demo(atomic_id,'Paid')
                check(demo_balance()==(51025,2),'Demo payment can safely retry after rolled-back storage failure')
                off_id,_=begin_demo('10')
                stop(); env['Demo__AutomaticTopUps']='false'; start()
                demo=Browser(base); demo.login('demo@example.com')
                check('StartDemoPayment' not in demo.request('/Wallet')[2],'Disabled demo payment is hidden from wallet')
                check(demo.post('/Wallet/StartDemoPayment',{'Amount':'10'},token_page='/Wallet')[0]==404 and demo.request('/Wallet/DemoPayment/'+off_id)[0]==404 and finish_demo(off_id,'Paid')[0]==404,'Disabled demo payment rejects all demo endpoints')
                check(demo_balance()==(51025,2),'Demo wallet credit persists after process restart')
                print('ALL WALLET AND DEMO PAYMENT CHECKS PASSED',flush=True)
            except Exception:
                stop(); log.flush(); print((Path(temp)/'server.log').read_text(encoding='utf-8')[-7000:]); raise
            finally: stop()


if __name__=='__main__': run()
