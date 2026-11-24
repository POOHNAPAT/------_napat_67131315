import {writeFile} from 'node:fs/promises';
import path from 'node:path';

export async function runSiteAudit({call,evaluate,navigate,screenshot,base,adminBase,output,setBase}) {
  const portals=[
    [base,['/','/Home/About','/Home/Contact','/Home/Membership','/Wallet','/Bookings/History','/Bookings/Result/1','/Accounting','/Receipts','/Courts','/Courts/Details/1','/Bookings/Create?courtId=1','/Account/Profile','/Account/EditProfile','/Bookings/ReportDamage']],
    [adminBase,['/Admin','/Admin/Bookings','/Admin/Booking/1','/Admin/Courts','/Admin/Schedule','/Admin/Customers','/Admin/SportTypes','/Admin/Equipment','/Admin/EquipmentItem/racket','/Admin/Inventory','/Admin/Plans','/Admin/Revenue','/Admin/Accounting','/Admin/Refunds','/Admin/Expenses','/Admin/DamageReports','/Admin/CheckIn','/Admin/EquipmentStock/racket','/Admin/Promotions']]
  ];
  const results=[];
  for(const width of [360,768,1440]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:950,deviceScaleFactor:1,mobile:width<680});
    for(const [portal,routes] of portals) {
      setBase(portal);
      for(const route of routes) {
        await navigate(route);
        await evaluate('document.fonts.ready.then(()=>true)');
        const result=await evaluate(`(() => ({title:document.title,main:!!document.querySelector('main h1'),overflow:document.documentElement.scrollWidth>innerWidth+1,brokenImages:[...document.images].filter(i=>i.complete&&i.naturalWidth===0).map(i=>i.getAttribute('src'))}))()`);
        results.push({portal:portal===base?'customer':'admin',route,width,...result});
        if(!result.main||result.overflow||result.brokenImages.length) {
          await screenshot('audit-failure-'+results.length);
          throw new Error('Site audit failed: '+JSON.stringify(results.at(-1)));
        }
        if((width===360&&['/Wallet','/Admin/CheckIn','/'].includes(route))||['/Admin/Booking/1','/Account/Profile','/Bookings/Result/1','/Admin/EquipmentItem/racket'].includes(route))await screenshot('audit-'+(portal===base?'customer':'admin')+'-'+route.replaceAll('/','-')+'-'+width);
      }
    }
  }
  await writeFile(path.join(output,'site-audit.json'),JSON.stringify(results,null,2));
  console.log(`PASS: ${results.length} page/viewport checks across ${portals.reduce((n,p)=>n+p[1].length,0)} routes, no overflow or broken images.`);
}
