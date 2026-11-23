"""Published Production build: separate public/admin services sharing an isolated database."""
from contextlib import closing
from pathlib import Path
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, check


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0));return sock.getsockname()[1]


def run():
    publish=ROOT/'artifacts/deploy-publish'
    assert (publish/'CourtBook.dll').exists(),'Run dotnet publish first'
    shutil.copy2(ROOT/'deploy/appsettings.Production.json',publish/'appsettings.Production.json')
    config=json.loads((publish/'appsettings.Production.json').read_text())
    assert config['Database']['Provider']=='Supabase' and not config['Demo']['SeedAccounts'] and not config['Demo']['AutomaticTopUps']
    with tempfile.TemporaryDirectory(prefix='courtbook-deploy-') as directory:
        temp=Path(directory);db=temp/'test.db';customer_port=port();admin_port=port()
        customer_base=f'http://127.0.0.1:{customer_port}';admin_base=f'http://127.0.0.1:{admin_port}'
        admin_password='Deploy-test-admin-123!'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Production','Database__Provider':'Sqlite','Database__Path':str(db),
             'Demo__SeedAccounts':'false','Demo__AutomaticTopUps':'false','Bootstrap__AdminEmail':'deploy-admin@example.com',
             'Bootstrap__AdminPassword':admin_password,'DataProtection__KeyDirectory':str(temp/'keys'),
             'AllowedHosts':'localhost;127.0.0.1','Account__PublicOrigin':customer_base,'Account__AdminOrigin':admin_base,
             'Logging__LogLevel__Default':'Warning','Logging__EventLog__LogLevel__Default':'None'}
        processes=[]
        def query(sql,args=()):
            with closing(sqlite3.connect(db)) as c:return c.execute(sql,args).fetchall()
        with (temp/'server.log').open('w',encoding='utf-8') as log:
            def start(base,admin):
                process=subprocess.Popen(['dotnet',str(publish/'CourtBook.dll'),'--urls',base],cwd=publish,
                    env={**env,'Portals__AdminPort':str(admin_port) if admin else '0'},stdout=log,stderr=subprocess.STDOUT)
                processes.append(process)
                for _ in range(100):
                    if process.poll() is not None:raise RuntimeError('Published host exited')
                    try:
                        if Browser(base).request('/healthz')[0]==200:return process
                    except OSError:pass
                    time.sleep(.1)
                raise RuntimeError('Published host did not become healthy')
            try:
                public=start(customer_base,False);private=start(admin_base,True)
                customer=Browser(customer_base);admin=Browser(admin_base)
                check(customer.request('/')[0]==200 and admin.request('/')[1]=='/Account/Login','Published build serves distinct customer and admin roots')
                check(customer.request('/Admin')[0]==404 and admin.request('/Account/Register')[0]==404,'Public and administrative services retain route isolation')
                check(customer.request('/healthz')[0]==200 and admin.request('/healthz')[0]==200,'Both services expose anonymous health checks')
                check(query("SELECT COUNT(*) FROM Users WHERE Role='Admin'")[0][0]==1 and query('SELECT COUNT(*) FROM Users')[0][0]==1,'Bootstrap creates one administrator without demo accounts')
                check(query('SELECT PasswordHash FROM Users')[0][0]!=admin_password,'Bootstrap password is hashed')
                check(admin.login('deploy-admin@example.com',admin_password)[1]=='/Admin','Bootstrapped administrator can sign in on admin service')
                customer.post('/Account/Register',{'Name':'Deployment User','Email':'deploy-user@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                check(customer.login('deploy-user@example.com')[1]=='/Courts','Customer registration and sign-in work in Production build')
                check(customer.post('/Wallet/StartDemoPayment',{'Amount':100},token_page='/Wallet')[0]==404,'Production deployment disables simulated payment credit')
                check(customer.request('/css/courtbook-dark.css')[0]==200,'Published static assets are included')
                check(list((temp/'keys').glob('*.xml'))!=[],'Data Protection keys use the configured directory')
                admin_hash=query("SELECT PasswordHash FROM Users WHERE Role='Admin'")[0][0]
                public.terminate();public.wait(timeout=10)
                env['Bootstrap__AdminPassword']='Different-unused-password-123!'
                start(customer_base,False)
                check(customer.request('/Bookings/History')[1]=='/Bookings/History','Persisted keys preserve the customer session across restart')
                check(query("SELECT PasswordHash FROM Users WHERE Role='Admin'")[0][0]==admin_hash,'Bootstrap restart does not overwrite an existing administrator')
                check(query('PRAGMA foreign_key_check')==[],'Deployment initialization preserves valid database relationships')
                print('ALL DEPLOYMENT SMOKE CHECKS PASSED',flush=True)
            except Exception:
                log.flush();print((temp/'server.log').read_text(encoding='utf-8')[-6000:]);raise
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.terminate()
                        try:process.wait(timeout=10)
                        except subprocess.TimeoutExpired:process.kill();process.wait()


if __name__=='__main__':run()
