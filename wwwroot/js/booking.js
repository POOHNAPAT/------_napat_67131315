(() => {
  const form = document.getElementById('booking-form');
  if (!form) return;
  const select = document.getElementById('PromotionId');
  const message = document.getElementById('quote-message');
  const button = document.getElementById('confirm-booking');
  const format = value => new Intl.NumberFormat('th-TH', {style:'currency',currency:'THB'}).format(value / 100);
  let request;
  async function update() {
    request?.abort();
    const current = request = new AbortController();
    const params = new URLSearchParams({CourtId:form.elements.CourtId.value,Date:form.elements.Date.value});
    form.querySelectorAll('[name=Hours]:checked:not(:disabled)').forEach(input => params.append('Hours',input.value));
    button.disabled = true;
    message.textContent = params.has('Hours') ? 'กำลังตรวจเวลาว่างและคำนวณยอด…' : 'เลือกช่วงเวลาเพื่อดูยอด';
    for(const id of ['gross','discount','net']) document.getElementById(id).textContent = '—';
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
      document.getElementById('gross').textContent = format(data.grossSatang);
      document.getElementById('discount').textContent = '− ' + format(data.discountSatang);
      document.getElementById('net').textContent = format(data.netSatang);
      message.textContent = cleared ? 'โปรโมชั่นเดิมไม่ตรงเงื่อนไข จึงคำนวณโดยไม่ใช้โปรโมชั่น กรุณาเลือกสิทธิ์ใหม่' : 'ยอดพร้อมแล้ว ระบบจะตรวจเวลาว่างอีกครั้งเมื่อยืนยัน';
      if(data.membershipName) message.textContent = 'ใช้ส่วนลด ' + data.membershipName + ' ซึ่งมากกว่าโปรโมชั่นที่เลือก';
      button.disabled = false;
    } catch(error) {
      if(error.name !== 'AbortError') message.textContent = error.message;
    }
  }
  form.addEventListener('change',update);
  form.addEventListener('slots-expired',update);
  update();
})();
