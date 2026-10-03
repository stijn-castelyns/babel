// Drives the web app in Chromium: setup, passkey, chat with an approval (step-up), triggers, settings.
// usage: node pwa.mjs BASE SETUPCODE OUTDIR   (BASE like http://localhost:17443; a model profile on mockllm.py)
import { createRequire } from 'module';
const require = createRequire('/opt/node22/lib/node_modules/');
const playwright = require('playwright');
const [base, code, out] = process.argv.slice(2);
const browser = await playwright.chromium.launch();
const context = await browser.newContext({ viewport: { width: 420, height: 860 } });   // a phone
const page = await context.newPage();
const problems = [];
page.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });
page.on('pageerror', (e) => problems.push(String(e)));
page.on('dialog', (d) => d.accept(d.type() === 'prompt' ? (d.defaultValue() || 'phone') : undefined));
await page.goto(base + '/');
const cdp = await context.newCDPSession(page);
await cdp.send('WebAuthn.enable');
await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
const shot = (name) => page.screenshot({ path: `${out}/${name}.png`, fullPage: true });
const step = (msg) => console.log('·', msg);

try {
await page.goto(`${base}/setup?code=${code}`);
await page.fill('input[name=userName]', 'sam');
await page.fill('input[name=password]', 'correct horse battery');
await page.click('button[type=submit]');
await page.waitForSelector('text=No passkey yet');
step('setup done, on settings');
await page.click('text=Add a passkey');
await page.waitForSelector('text=Passkey added');
await page.waitForSelector('li:has-text("phone")');
step('passkey registered');
await shot('settings');

await page.click('nav >> text=Chat');
await page.waitForSelector('select[name=workspace]');
await page.click('text=New session');
await page.waitForSelector('textarea');
await page.fill('textarea', 'please list the files');
await page.click('text=Send');
await page.waitForSelector('text=── succeeded');
step('chat reply: ' + (await page.locator('.msg:not(.user) .text').last().innerText()).split('\n')[0]);

await page.fill('textarea', 'please run shell');
await page.click('text=Send');
await page.waitForSelector('.card.approval');
step('approval card shown');
await shot('approval');
await page.click('.card.approval >> text=Approve');
await page.waitForSelector('text=✓ approved');
await page.waitForSelector('text=── succeeded >> nth=1');
step('approved after an automatic passkey step-up');
await shot('chat');

await page.click('nav >> text=Dashboard');
await page.waitForSelector('h2:has-text("Recent runs")');
await shot('dashboard');
await page.click('nav >> text=Triggers');
await page.waitForSelector('td:has-text("ping")');
step('triggers listed');
await shot('triggers');
await page.click('nav >> text=Settings');
await page.click('text=Show devices and tokens');
await page.waitForSelector('h2:has-text("Tokens and paired devices")');
step('devices: ' + (await page.locator('table tr').count() - 1) + ' token(s)');
const sw = await page.evaluate(async () => (await navigator.serviceWorker.getRegistration())?.active?.state ?? 'none');
step('service worker: ' + sw);
step('problems: ' + (problems.length ? problems.join(' | ') : 'none'));
} catch (e) {
  console.log('FAILED:', String(e).split('\n')[0]);
  console.log('toast:', await page.locator('#toast').innerText().catch(() => ''));
  console.log('main:', (await page.locator('main').innerText().catch(() => '')).slice(0, 400));
  console.log('problems:', problems.join(' | '));
  await page.screenshot({ path: `${out}/failure.png`, fullPage: true });
  process.exitCode = 1;
}
await browser.close();
