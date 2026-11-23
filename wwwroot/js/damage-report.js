(() => {
  const category = document.getElementById('damage-category');
  const equipment = document.getElementById('damage-equipment');
  if (!category || !equipment) return;
  function update() {
    const enabled = category.value === 'Equipment';
    document.getElementById('damage-equipment-field').hidden = !enabled;
    equipment.disabled = !enabled;
    equipment.required = enabled;
  }
  category.addEventListener('change', update);
  update();
})();
