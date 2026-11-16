// The two small pages the twin serves itself: making invitations and accepting one.
// They are plain HTML with no outside files, and talk to the twin's own API.

const SHELL = (title, body, script) => `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<meta name="referrer" content="no-referrer">
<title>${title}</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; background: #101010; color: #e8e8e8; font: 16px/1.5 system-ui, sans-serif; }
  main { max-width: 34rem; margin: 0 auto; padding: 2.5rem 1rem; }
  h1 { font-size: 1.5rem; margin: 0 0 .25rem; }
  p { color: #b5b5b5; }
  label { display: block; margin: 1rem 0 .25rem; }
  fieldset { margin: 1rem 0 0; padding: .5rem .75rem; border: 1px solid #3a3a3a; border-radius: .3rem; }
  fieldset label { margin: .35rem 0; }
  fieldset input { width: auto; margin-right: .5rem; }
  input, select { width: 100%; box-sizing: border-box; padding: .65rem; border: 1px solid #3a3a3a; border-radius: .3rem; background: #1c1c1c; color: inherit; font: inherit; }
  button { margin-top: 1.25rem; padding: .65rem 1.2rem; border: 0; border-radius: .3rem; background: #3aa0d8; color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
  button.quiet { margin: 0; padding: .3rem .7rem; background: #333; font-weight: 400; }
  button:disabled { opacity: .5; cursor: default; }
  .note { margin-top: 1rem; padding: .75rem 1rem; border-radius: .3rem; background: #1c1c1c; overflow-wrap: anywhere; }
  .bad { color: #ff8a80; }
  .good { color: #9be29b; }
  table { width: 100%; border-collapse: collapse; margin-top: 1rem; font-size: .9rem; }
  td, th { text-align: left; padding: .4rem .5rem .4rem 0; border-bottom: 1px solid #2a2a2a; }
  a { color: #6cc0f0; }
  [hidden] { display: none; }
</style>
</head>
<body><main>${body}</main>
<script>
${script}
</script>
</body>
</html>`;

const COMMON = `
const $ = id => document.getElementById(id);
const show = (id, text, kind) => { const el = $(id); el.textContent = text; el.className = 'note ' + (kind || ''); el.hidden = !text; };
async function api(method, path, body, token) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = 'MediaBrowser Client="JellyWing Twin", Device="Browser", DeviceId="twin-pages", Version="1", Token="' + token + '"';
  const response = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!response.ok) throw new Error((data && data.error) || 'The twin answered ' + response.status + '.');
  return data;
}`;

/** The page an invited person lands on. The code is in the address, after the #, so it is never sent in a request line. */
export const acceptPage = serverName => SHELL(`Join ${serverName}`, `
<h1>Join ${serverName}</h1>
<p id="lead">Checking your invitation…</p>
<form id="form" hidden>
  <label for="name">Your name</label>
  <input id="name" autocomplete="username" maxlength="64" required>
  <label for="password">Password</label>
  <input id="password" type="password" autocomplete="new-password" minlength="8" required>
  <label for="again">Password again</label>
  <input id="again" type="password" autocomplete="new-password" minlength="8" required>
  <button id="go">Create my account</button>
</form>
<div id="message" class="note" hidden></div>
<p id="done" hidden><a href="/web/">Sign in and start watching</a></p>`, `${COMMON}
const code = location.hash.slice(1);
(async () => {
  try {
    const answer = await api('GET', '/Twin/Invitations/Check/' + encodeURIComponent(code));
    if (!answer.Valid) throw new Error('This invitation is not valid any more. Ask for a new one.');
    $('lead').textContent = answer.InvitedBy + ' invited you. Pick a name and a password.';
    $('form').hidden = false;
  } catch (error) {
    $('lead').hidden = true;
    show('message', error.message, 'bad');
  }
})();
$('form').addEventListener('submit', async event => {
  event.preventDefault();
  if ($('password').value !== $('again').value) return show('message', 'The two passwords are not the same.', 'bad');
  $('go').disabled = true;
  try {
    const answer = await api('POST', '/Twin/Invitations/Accept', { Code: code, Name: $('name').value, Password: $('password').value });
    $('form').hidden = true;
    $('lead').hidden = true;
    show('message', 'Welcome, ' + answer.Name + '. Your account is ready.', 'good');
    $('done').hidden = false;
  } catch (error) {
    show('message', error.message, 'bad');
    $('go').disabled = false;
  }
});`);

/** The page an administrator uses. It borrows the sign-in the web client keeps for this address. */
export const invitePage = serverName => SHELL(`Invite someone to ${serverName}`, `
<h1>Invite someone to ${serverName}</h1>
<p>Make a link, send it to one person. It works once. They pick their own name and password, and can watch as soon as they have.</p>
<div id="signin" class="note" hidden>You need to be signed in as an administrator. <a href="/web/">Sign in</a>, then come back to this page.</div>
<form id="form" hidden>
  <label for="days">The link works for</label>
  <select id="days"><option value="1">1 day</option><option value="7" selected>7 days</option><option value="30">30 days</option></select>
  <fieldset id="libraries"><legend>They can see</legend></fieldset>
  <button id="go">Make a link</button>
</form>
<div id="message" class="note" hidden></div>
<div id="link" class="note" hidden></div>
<table id="list" hidden><thead><tr><th>Made</th><th>By</th><th>Can see</th><th>State</th><th></th></tr></thead><tbody></tbody></table>`, `${COMMON}
let token = null;
try {
  const server = (JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers || []).find(s => s.AccessToken);
  token = server && server.AccessToken;
} catch { /* nothing stored */ }

async function refresh() {
  const rows = await api('GET', '/Twin/Invitations', null, token);
  const body = $('list').tBodies[0];
  body.textContent = '';
  for (const row of rows) {
    const tr = body.insertRow();
    tr.insertCell().textContent = new Date(row.Created).toLocaleDateString();
    tr.insertCell().textContent = row.CreatedBy;
    tr.insertCell().textContent = row.Libraries ? row.Libraries.join(', ') : 'Everything';
    tr.insertCell().textContent = row.State === 'Used' ? 'Used by ' + row.UsedBy : row.State === 'Open' ? 'Open until ' + new Date(row.Expires).toLocaleDateString() : row.State;
    const cell = tr.insertCell();
    if (row.State === 'Open') {
      const button = document.createElement('button');
      button.className = 'quiet';
      button.textContent = 'Withdraw';
      button.onclick = async () => { await api('DELETE', '/Twin/Invitations/' + row.Id, null, token); refresh(); };
      cell.appendChild(button);
    }
  }
  $('list').hidden = rows.length === 0;
}

(async () => {
  if (!token) { $('signin').hidden = false; return; }
  try {
    await refresh();
    const views = await api('GET', '/UserViews', null, token);
    for (const view of views.Items) {
      const label = document.createElement('label');
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = true;
      box.value = view.Name;
      label.append(box, view.Name);
      $('libraries').appendChild(label);
    }
    $('form').hidden = false;
  } catch (error) {
    $('signin').hidden = false;
    show('message', error.message, 'bad');
  }
})();

$('form').addEventListener('submit', async event => {
  event.preventDefault();
  $('go').disabled = true;
  try {
    const libraries = [...$('libraries').querySelectorAll('input:checked')].map(box => box.value);
    const made = await api('POST', '/Twin/Invitations', { Days: Number($('days').value), Libraries: libraries }, token);
    const url = location.origin + '/invite/accept#' + made.Code;
    show('link', url, 'good');
    show('message', 'Send this link to one person. It is shown only now.', '');
    await refresh();
  } catch (error) {
    show('message', error.message, 'bad');
  }
  $('go').disabled = false;
});`);
