const statusText = document.querySelector<HTMLElement>('#api-status');
const statusRegion = statusText?.parentElement;
const retryButton = document.querySelector<HTMLButtonElement>('#retry');

async function checkApi(): Promise<void> {
  if (!statusText || !statusRegion || !retryButton) return;

  retryButton.disabled = true;
  statusRegion.dataset.state = 'checking';
  statusText.textContent = 'Checking API…';

  try {
    const response = await fetch('/health/live', { cache: 'no-store' });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const body: unknown = await response.json();
    if (typeof body !== 'object' || body === null || !('status' in body) || body.status !== 'ok') {
      throw new Error('Unexpected health response');
    }
    statusRegion.dataset.state = 'online';
    statusText.textContent = 'API online';
  } catch {
    statusRegion.dataset.state = 'offline';
    statusText.textContent = 'API unavailable';
  } finally {
    retryButton.disabled = false;
  }
}

retryButton?.addEventListener('click', checkApi);
void checkApi();
