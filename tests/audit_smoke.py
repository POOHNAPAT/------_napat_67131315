"""Regression checks for issues found during the current site audit."""
from contextlib import closing
from pathlib import Path
import datetime as dt
import html
import os
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check
from product_smoke import port


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-audit-') as directory:
        database=Path(directory)/'test.db';base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(database),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Logging__LogLevel__Default':'Warning','Demo__AutomaticTopUps':'true'}
        def sql(statement,args=()):
            with closing(sqlite3.connect(database)) as c:rows=c.execute(statement,args).fetchall();c.commit();return rows
        with (Path(directory)/'server.log').open('w',encoding='utf-8') as log:
            server=None
            def start():
                nonlocal server
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(150):
                    try:
                        if Browser(base).request('/')[0]==200:return
                    except OSError:pass
                    if server.poll() is not None:raise RuntimeError('Host exited')
                    time.sleep(.1)
                raise RuntimeError('Host not ready')
            def stop():
                if server and server.poll() is None:server.terminate();server.wait(timeout=15)
            try:
                start();member=Browser(base)
                member.post('/Account/Register',{'Name':'Audit Player','Email':'audit@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD});member.login('audit@example.com')
                _,checkout,_=member.post('/Wallet/StartDemoPayment',{'Amount':1000},token_page='/Wallet')
                member.post('/Wallet/CompleteDemoPayment',{'id':checkout.rsplit('/',1)[1],'outcome':'Paid'},token_page=checkout)
                _,result,_=member.post('/Bookings/Create',{'CourtId':1,'Date':str(TODAY+dt.timedelta(days=3)),'Hours':[10]});booking=int(result.rsplit('/',1)[1])
                balance=sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY-dt.timedelta(days=1)),booking))
                page=member.request(result)[2]
                member.post(f'/Bookings/PayWallet/{booking}',{},token_page=result)
                check(sql('SELECT PaidAt FROM Bookings WHERE Id=?',(booking,))[0][0] is None and sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]==balance,'Expired booking cannot debit the wallet or become paid')
                check('action="/Bookings/PayWallet/' not in page and 'id="wallet-payment"' not in page,'Expired booking hides payment actions')
                wallet=member.request(f'/Wallet?bookingId={booking}')[2]
                check('รายการนี้ไม่มียอดรอชำระ' in html.unescape(wallet),'Expired checkout does not suggest topping up an expired booking')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY+dt.timedelta(days=3)),booking))
                member.post(f'/Bookings/PayWallet/{booking}',{},token_page=result)
                check(sql('SELECT PaidAt FROM Bookings WHERE Id=?',(booking,))[0][0] is not None,'Future valid booking still accepts wallet payment')
                wallet=member.request('/Wallet')[2]
                check('StartDemoPayment' in wallet and 'id="topup-slip"' not in wallet and 'id="topup-requests"' not in wallet,'Current wallet offers demo credit without an empty legacy transfer approval panel')
                stop();env['Demo__AutomaticTopUps']='false';start();member=Browser(base);member.login('audit@example.com')
                wallet=html.unescape(member.request('/Wallet')[2])
                check('StartDemoPayment' not in wallet and 'href="#topup"' not in wallet and 'ยังไม่เปิดให้บริการ' in wallet,'Disabled top-up mode gives a clear message instead of a dead-end wallet')
                check(sql('PRAGMA foreign_key_check')==[],'Audit fixes preserve database relationships')
                print('ALL AUDIT CHECKS PASSED',flush=True)
            finally:stop()


if __name__=='__main__':run()
