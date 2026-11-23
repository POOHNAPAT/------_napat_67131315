(() => {
  const filters = document.querySelector('.sport-filters');
  if (!filters) return;
  const cards = [...document.querySelectorAll('[data-court-sport]')];
  filters.addEventListener('click', event => {
    const button = event.target.closest('button[data-sport]');
    if (!button) return;
    for (const filter of filters.querySelectorAll('button')) {
      filter.setAttribute('aria-pressed', String(filter === button));
    }
    for (const card of cards) {
      card.hidden = button.dataset.sport !== 'all' && card.dataset.courtSport !== button.dataset.sport;
    }
    document.getElementById('court-filter-status').textContent = `แสดง ${cards.filter(card => !card.hidden).length} สนาม`;
  });
})();
