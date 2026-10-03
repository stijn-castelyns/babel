// End-to-end check of the daemon's browser sign-in with a CDP virtual authenticator.
import { createRequire } from 'module';
const require = createRequire('/opt/node22/lib/node_modules/');
let playwright;
try { playwright = require('playwright'); } catch { playwright = require('/opt/node-tools/node_modules/playwright'); }
const [base, code] = process.argv.slice(2);
const browser = await playwright.chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();
await page.goto(base + '/');
const cdp = await context.newCDPSession(page);
await cdp.send('WebAuthn.enable');
const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
  protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });

const call = (path, body, method = 'POST') => page.evaluate(async ([path, body, method]) => {
  const r = await fetch(path, { method, headers: body === undefined ? {} : { 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  return { status: r.status, body: text };
}, [path, body, method]);
const register = () => page.evaluate(async () => {
  const o = await fetch('/auth/passkey/creation-options', { method: 'POST' });
  if (o.status !== 200) return { status: o.status, body: 'options: ' + await o.text() };
  const opts = await o.json();
  const cred = await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(opts) });
  const r = await fetch('/auth/passkey/register', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ credential: JSON.stringify(cred.toJSON()), name: 'test key' }) });
  return { status: r.status, body: await r.text() };
});
const assert = () => page.evaluate(async () => {
  const opts = await (await fetch('/auth/passkey/request-options', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' })).json();
  const cred = await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(opts) });
  const r = await fetch('/auth/passkey/signin', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ credential: JSON.stringify(cred.toJSON()) }) });
  return { status: r.status, body: await r.text() };
});
const show = (label, r) => console.log(label.padEnd(34), r.status, r.body.slice(0, 150));

show('setup with a wrong code', await call('/auth/setup', { code: 'nope', userName: 'sam', password: 'correct horse battery' }));
show('setup', await call('/auth/setup', { code, userName: 'sam', password: 'correct horse battery' }));
show('setup again', await call('/auth/setup', { code, userName: 'eve', password: 'correct horse battery' }));
show('status (password session)', await call('/auth/status', undefined, 'GET'));
show('register first passkey', await register());
show('register a second (no step-up)', await register());
show('sessions (read)', { ...(await call('/api/sessions?limit=1', undefined, 'GET')), body: '…' });
show('admin without step-up', await call('/api/retention/sweep', { dryRun: true }));
show('step-up with the passkey', await assert());
show('status (stepped up)', await call('/auth/status', undefined, 'GET'));
show('admin after step-up', await call('/api/retention/sweep', { dryRun: true }));
show('sign out', await call('/auth/signout', {}));
show('sessions signed out', await call('/api/sessions', undefined, 'GET'));
show('passkey sign-in (discoverable)', await assert());
show('status (passkey session)', await call('/auth/status', undefined, 'GET'));
show('passkeys', await call('/auth/passkeys', undefined, 'GET'));
await call('/auth/signout', {});
show('wrong password', await call('/auth/password', { userName: 'sam', password: 'wrong wrong wrong' }));
show('password sign-in', await call('/auth/password', { userName: 'sam', password: 'correct horse battery' }));
await browser.close();
