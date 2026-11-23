(() => {
  const form = document.getElementById('booking-form');
  if (!form) return;
  const select = document.getElementById('PromotionId');
  const message = document.getElementById('quote-message');
  const button = document.getElementById('confirm-booking');
  const format = value => new Intl.NumberFormat('th-TH', {style:'currency',currency:'THB'}).format(value / 100);
  let request;
  const rangeButton=document.getElementById('apply-time-range');
  rangeButton?.addEventListener('click',()=>{
    const start=Number(document.getElementById('start-hour').value),end=Number(document.getElementById('end-hour').value);
    const inputs=[...form.querySelectorAll('[name=Hours]')];
    const chosen=inputs.filter(input=>Number(input.value)>=start && Number(input.value)<end);
    const status=document.getElementById('time-range-message');
    if(!start || !end || end<=start || chosen.length!==end-start || chosen.some(input=>input.disabled)){
      status.textContent='ช่วงเวลานี้เลือกไม่ได้ กรุณาเลือกเวลาสิ้นสุดหลังเวลาเริ่มต้น และทุกชั่วโมงต้องว่าง';return;
    }
    inputs.forEach(input=>input.checked=chosen.includes(input));status.textContent='เลือกช่วงเวลาแล้ว';update();
  });
  async function update() {
    request?.abort();
    const current = request = new AbortController();
    const params = new URLSearchParams({CourtId:form.elements.CourtId.value,Date:form.elements.Date.value,Players:form.elements.Players.value});
    form.querySelectorAll('[name=Hours]:checked:not(:disabled)').forEach(input => params.append('Hours',input.value));
    form.querySelectorAll('[name^="Equipment["]').forEach(input => params.append(input.name, input.value));
    const selected=[...form.querySelectorAll('[name=Hours]:checked:not(:disabled)')].map(input=>Number(input.value)).sort((a,b)=>a-b);
    const time=document.getElementById('selected-time');
    if(time)time.textContent=selected.length ? `เวลา ${String(selected[0]).padStart(2,'0')}:00–${String(selected.at(-1)+1).padStart(2,'0')}:00 · ${selected.length} ชั่วโมง` : '';
    button.disabled = true;
    message.textContent = params.has('Hours') ? 'กำลังตรวจเวลาว่างและคำนวณยอด…' : 'เลือกช่วงเวลาเพื่อดูยอด';
    for(const id of ['court-total','equipment-total','gross','discount','net']) document.getElementById(id).textContent = '—';
    if(!params.has('Hours')) return;
    try {
      async function quote() {
        const response = await fetch('/Bookings/Quote?' + params, {signal:current.signal,headers:{Accept:'application/json'}});
        if(response.redirected) throw new Error('กรุณาเข้าสู่ระบบอีกครั้ง');
        const data = await response.json();
        if(!response.ok) throw new Error(data.error || 'ไม่สามารถตรวจราคาได้');
        return data;
      }
      let data = await quote();
      let cleared = false;
      for(const option of select.options) {
        if(!option.value) continue;
        const promo = data.promotions.find(p => String(p.id) === option.value);
        option.disabled = !promo?.eligible;
        option.textContent = promo ? `${promo.name} · ${promo.discountLabel}${promo.eligible?'':' (ยังไม่ตรงเงื่อนไข)'}` : 'ไม่พร้อมใช้งาน';
        if(option.selected && option.disabled) {select.value=''; cleared=true;}
      }
      if(select.value) {params.set('PromotionId',select.value); data = await quote();}
      if(current !== request) return;
      document.getElementById('court-total').textContent = format(data.grossSatang - data.equipmentSatang);
      document.getElementById('equipment-total').textContent = format(data.equipmentSatang);
      document.getElementById('gross').textContent = format(data.grossSatang);
      document.getElementById('discount').textContent = '− ' + format(data.discountSatang);
      document.getElementById('net').textContent = format(data.netSatang);
      message.textContent = cleared ? 'โปรโมชั่นเดิมไม่ตรงเงื่อนไข จึงคำนวณโดยไม่ใช้โปรโมชั่น กรุณาเลือกสิทธิ์ใหม่' : 'ยอดพร้อมแล้ว ระบบจะตรวจเวลาว่างอีกครั้งเมื่อยืนยัน';
      if(data.membershipName) message.textContent = data.includedHours>0
        ? 'ใช้โควตา ' + data.membershipName + ' จำนวน ' + data.includedHours + ' ชั่วโมง · คงเหลือหลังจอง ' + (data.remainingQuotaHours-data.includedHours) + ' ชั่วโมงในรอบวันที่เล่น · ค่าอุปกรณ์คิดแยก'
        : 'ใช้ส่วนลด ' + data.membershipName + ' ซึ่งมากกว่าโปรโมชั่นที่เลือก';
      form.querySelector('[name=ExpectedIncludedHours]').value=data.includedHours;
      button.disabled = false;
    } catch(error) {
      if(error.name !== 'AbortError') message.textContent = error.message;
    }
  }
  form.addEventListener('change',update);
  form.addEventListener('slots-expired',update);
  update();
})();
