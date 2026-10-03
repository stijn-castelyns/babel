// Checks the service worker's push handling: delivers a message through CDP (as a push service would after decrypting)
// and reads back the notification it shows. Also tries a real subscription, which needs the browser's push service.
// usage: node push.mjs BASE USER PASSWORD
import { createRequire } from 'module';
const require = createRequire('/opt/node22/lib/node_modules/');
const playwright = require('playwright');
const [base, user, password] = process.argv.slice(2);
const browser = await playwright.chromium.launch({ channel: 'chromium' });   // full Chromium: the headless shell has no notifications
const context = await browser.newContext();
await context.grantPermissions(['notifications'], { origin: base });
const page = await context.newPage();
await page.goto(base + '/');
await page.evaluate(async ([user, password]) => {
  await fetch('/auth/password', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ userName: user, password }) });
}, [user, password]);
const cdp = await context.newCDPSession(page);
const registered = new Promise((resolve) => cdp.on('ServiceWorker.workerRegistrationUpdated', (e) => {
  for (const r of e.registrations) if (r.scopeURL.startsWith(base) && !r.isDeleted) resolve(r.registrationId);
}));
await cdp.send('ServiceWorker.enable');
await page.reload();
await page.evaluate(() => navigator.serviceWorker.ready);
const registrationId = await registered;
await cdp.send('ServiceWorker.deliverPushMessage', {
  origin: new URL(base).origin, registrationId,
  data: JSON.stringify({ title: 'Approval needed: shell', body: 'git push', url: '/#/run/r_test', tag: 'q_test' }),
});
await page.waitForTimeout(1000);
const shown = await page.evaluate(async () => (await (await navigator.serviceWorker.ready).getNotifications()).map((n) => ({ title: n.title, body: n.body, url: n.data?.url })));
console.log('notifications:', JSON.stringify(shown));
const key = await page.evaluate(async () => (await (await fetch('/api/push/key')).json()).publicKey);
console.log('server key length:', Buffer.from(key, 'base64url').length);
const subscribe = await page.evaluate(async (publicKey) => {
  try {
    const reg = await navigator.serviceWorker.ready;
    const key = Uint8Array.from(atob(publicKey.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
    const s = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    return 'subscribed: ' + new URL(s.endpoint).host;
  } catch (e) { return 'subscribe failed: ' + e; }
}, key);
console.log(subscribe);
await browser.close();
