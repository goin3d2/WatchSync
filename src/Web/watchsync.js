define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (loading) {
    'use strict';

    var pluginId = '5a5eb58d-11a9-44c6-af4e-c1e48794c63c';
    var BOOLS = ['SyncWatched', 'SyncProgress', 'SyncRemovals', 'DryRun'];
    var NUMS = ['PollMinutes', 'FullSyncHours', 'EmbyWatchedPercent', 'PlexWatchedPercent', 'MinProgressSeconds'];
    var KEYNOTE = {
        local: 'Uses this server directly; nothing to enter.',
        emby: 'An API key from that server: Dashboard → Advanced → API Keys. Leave blank to keep the saved one.',
        plex: 'The server owner\'s Plex token (PlexOnlineToken in Plex\'s Preferences.xml). Users the server is shared with are found through plex.tv. Leave blank to keep the saved one.'
    };

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }
    function fmtN(n) { return n == null ? '–' : Number(n).toLocaleString(); }
    function ago(t, now) {
        if (!t) { return 'never'; }
        var d = Math.max(0, (now || Date.now() / 1000) - t);
        if (d < 60) { return Math.round(d) + 's ago'; }
        if (d < 3600) { return Math.round(d / 60) + ' min ago'; }
        if (d < 86400) { return (d / 3600).toFixed(1) + ' h ago'; }
        return (d / 86400).toFixed(1) + ' days ago';
    }
    function when(t) {
        if (!t) { return ''; }
        return new Date(t * 1000).toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    }
    function toast(msg) { require(['toast'], function (t) { t(msg); }); }
    function getJson(path) { return ApiClient.getJSON(ApiClient.getUrl(path)); }
    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST', url: ApiClient.getUrl(path), data: JSON.stringify(body || {}),
            contentType: 'application/json', dataType: 'json'
        });
    }

    return function (view) {
        var $ = function (s) { return view.querySelector(s); };
        var $$ = function (s) { return Array.prototype.slice.call(view.querySelectorAll(s)); };
        var config = null;
        var users = { A: null, B: null };
        var timer = null, logAfter = 0;

        // ---- tabs ------------------------------------------------------------------------
        $$('.ws-tab').forEach(function (b) {
            b.addEventListener('click', function () {
                $$('.ws-tab').forEach(function (x) { x.classList.toggle('on', x === b); });
                $$('.ws-pane').forEach(function (p) { p.classList.toggle('on', p.getAttribute('data-pane') === b.getAttribute('data-tab')); });
                if (b.getAttribute('data-tab') === 'log') { tickLog(); }
                if (b.getAttribute('data-tab') === 'cw') { fillCwPairs(); loadCompare(); }
            });
        });

        // ---- config ----------------------------------------------------------------------
        function fetchConfig() {
            return ApiClient.getPluginConfiguration(pluginId).then(function (c) { config = c; return c; });
        }
        function saveConfig(c) {
            loading.show();
            return ApiClient.updatePluginConfiguration(pluginId, c).then(function (r) {
                loading.hide(); toast('Saved'); config = c; return r;
            }, function () { loading.hide(); toast('Couldn\'t save'); });
        }

        function srcCard(side) { return view.querySelector('[data-src="' + side + '"]'); }
        function srcField(side, f) { return srcCard(side).querySelector('[data-f="' + f + '"]'); }
        function showKind(side) {
            var kind = srcField(side, 'Kind').value;
            var card = srcCard(side);
            card.querySelectorAll('.ws-url,.ws-key').forEach(function (el) { el.style.display = kind === 'local' ? 'none' : ''; });
            var saved = config && config['Source' + side + 'Key'];
            card.querySelector('.ws-keynote').textContent = KEYNOTE[kind] + (kind !== 'local' && saved ? ' (A key is saved.)' : '');
        }

        function fillConfig(c) {
            ['A', 'B'].forEach(function (s) {
                srcField(s, 'Kind').value = c['Source' + s + 'Kind'] || (s === 'A' ? 'local' : 'plex');
                srcField(s, 'Url').value = c['Source' + s + 'Url'] || '';
                srcField(s, 'Key').value = '';
                showKind(s);
            });
            BOOLS.forEach(function (k) { $('#' + k).checked = !!c[k]; });
            NUMS.forEach(function (k) { $('#' + k).value = c[k]; });
            $('#Direction').value = c.Direction || 'both';
            renderPairRows(c.Pairs || []);
        }

        ['A', 'B'].forEach(function (s) {
            srcField(s, 'Kind').addEventListener('change', function () { users[s] = null; showKind(s); renderPairRows(readPairs()); });
            srcCard(s).querySelector('.ws-test').addEventListener('click', function () { testSource(s, true); });
        });

        function testSource(side, fromPage) {
            var out = srcCard(side).querySelector('.ws-result');
            out.textContent = 'Testing…'; out.className = 'ws-result ws-small ws-muted';
            var body = { Side: side };
            if (fromPage) {
                body.Kind = srcField(side, 'Kind').value;
                body.Url = srcField(side, 'Url').value.trim();
                body.Key = srcField(side, 'Key').value.trim();
            }
            return postJson('watchsync/test', body).then(function (r) {
                if (r.ok) {
                    users[side] = r.users;
                    out.textContent = '✓ ' + r.message; out.className = 'ws-result ws-small ws-ok';
                } else {
                    out.textContent = '✗ ' + r.message; out.className = 'ws-result ws-small ws-num-bad';
                }
                renderPairRows(readPairs());
            }, function () { out.textContent = '✗ The test request failed'; out.className = 'ws-result ws-small ws-num-bad'; });
        }

        function userOptions(side, selected, savedName) {
            var list = users[side] || [];
            var opts = '<option value="">Choose a user…</option>';
            var found = false;
            list.forEach(function (u) {
                var sel = u.id === selected; if (sel) { found = true; }
                opts += '<option value="' + esc(u.id) + '"' + (sel ? ' selected' : '') + '>' + esc(u.name) + '</option>';
            });
            if (selected && !found) { opts += '<option value="' + esc(selected) + '" selected>' + esc(savedName || selected) + '</option>'; }
            return opts;
        }

        function renderPairRows(pairs) {
            var box = $('#wsPairRows');
            if (!pairs.length) { box.innerHTML = '<p class="ws-muted ws-small">No pairs yet.</p>'; return; }
            box.innerHTML = pairs.map(function (p, i) {
                return '<div class="ws-pair" data-i="' + i + '">' +
                    '<select class="ws-native" data-p="A">' + userOptions('A', p.UserA, p.NameA) + '</select>' +
                    '<span>↔</span>' +
                    '<select class="ws-native" data-p="B">' + userOptions('B', p.UserB, p.NameB) + '</select>' +
                    '<label class="checkboxContainer" style="margin:0"><input is="emby-checkbox" type="checkbox" data-p="on"' + (p.Enabled !== false ? ' checked' : '') + ' /><span>On</span></label>' +
                    '<button is="emby-button" type="button" class="raised" data-p="del"><span>Remove</span></button></div>';
            }).join('');
        }

        function readPairs() {
            return $$('.ws-pair').map(function (row) {
                var a = row.querySelector('[data-p="A"]'), b = row.querySelector('[data-p="B"]');
                var on = row.querySelector('[data-p="on"]');
                var name = function (sel) { var o = sel.options[sel.selectedIndex]; return o && o.value ? o.textContent : ''; };
                return { UserA: a.value, UserB: b.value, NameA: name(a), NameB: name(b), Enabled: on ? on.checked : true };
            });
        }

        $('#wsPairRows').addEventListener('click', function (e) {
            var del = e.target.closest('[data-p="del"]');
            if (!del) { return; }
            var pairs = readPairs();
            pairs.splice(+del.closest('.ws-pair').getAttribute('data-i'), 1);
            renderPairRows(pairs);
        });
        $('#wsAddPair').addEventListener('click', function () {
            var pairs = readPairs();
            pairs.push({ UserA: '', UserB: '', NameA: '', NameB: '', Enabled: true });
            renderPairRows(pairs);
        });

        $('#wsSaveSources').addEventListener('click', function () {
            fetchConfig().then(function (c) {
                ['A', 'B'].forEach(function (s) {
                    var kind = srcField(s, 'Kind').value;
                    c['Source' + s + 'Kind'] = kind;
                    c['Source' + s + 'Url'] = srcField(s, 'Url').value.trim();
                    var key = srcField(s, 'Key').value.trim();
                    if (key) { c['Source' + s + 'Key'] = key; }
                    if (kind === 'local') { c['Source' + s + 'Key'] = ''; c['Source' + s + 'Url'] = ''; }
                });
                if (c.SourceAKind === 'local' && c.SourceBKind === 'local') { toast('Source A and Source B can\'t both be this server'); return; }
                var pairs = readPairs().filter(function (p) { return p.UserA && p.UserB; });
                c.Pairs = pairs;
                saveConfig(c).then(function () { fillConfig(c); });
            });
        });

        $('#wsSaveSettings').addEventListener('click', function () {
            fetchConfig().then(function (c) {
                BOOLS.forEach(function (k) { c[k] = $('#' + k).checked; });
                NUMS.forEach(function (k) { var v = parseInt($('#' + k).value, 10); if (!isNaN(v)) { c[k] = v; } });
                c.Direction = $('#Direction').value;
                saveConfig(c);
            });
        });

        $('#wsReset').addEventListener('click', function () {
            if (!window.confirm('Forget what was synced? The next pass compares every title again.')) { return; }
            postJson('watchsync/reset').then(function () { toast('Sync state cleared'); tick(); });
        });

        // ---- status ----------------------------------------------------------------------
        function run(full, apply, pair) {
            postJson('watchsync/run', { Full: !!full, Apply: !!apply, Pair: pair == null ? -1 : pair }).then(function (r) {
                toast(r.ok ? (apply ? 'Applying the preview…' : 'Sync started') : r.message);
                setTimeout(tick, 600);
            });
        }
        $('#wsRun').addEventListener('click', function () { run(false, false); });
        $('#wsFull').addEventListener('click', function () { run(true, false); });
        $('#wsApply').addEventListener('click', function () {
            if (!window.confirm('Write the previewed changes to both servers now?')) { return; }
            run(true, true);
        });

        function table(rows, cols) {
            if (!rows.length) { return '<p class="ws-muted ws-small">None.</p>'; }
            return '<div class="ws-tablewrap"><table><thead><tr>' + cols.map(function (c) { return '<th>' + c[0] + '</th>'; }).join('') +
                '</tr></thead><tbody>' + rows.map(function (r) {
                    return '<tr>' + cols.map(function (c) { return '<td>' + c[1](r) + '</td>'; }).join('') + '</tr>';
                }).join('') + '</tbody></table></div>';
        }

        function renderStatus(s) {
            var busy = s.running;
            $('#wsWave').classList.toggle('busy', busy);
            $('#wsPill').textContent = busy ? 'Syncing' : (s.dry_run ? 'Preview mode' : 'Watching for changes');
            $('#wsPhase').textContent = busy ? 'Syncing' : 'Idle';
            $('#wsDetail').textContent = busy ? s.phase : (s.dry_run ? 'Preview mode: changes are worked out but not written.' : 'Changes are checked every few minutes; plays on this server within a minute.');
            $('#wsRun').disabled = $('#wsFull').disabled = $('#wsApply').disabled = busy;
            var anyPlan = s.pairs.some(function (p) { return p.plan && p.plan.length; });
            $('#wsApply').style.display = s.dry_run && anyPlan ? '' : 'none';

            var alerts = [];
            if (!s.pairs.length) { alerts.push('<div class="ws-alert warn">No user pairs yet. Set up the two sources and pick users on the <b>Sources &amp; users</b> tab.</div>'); }
            if (s.dry_run && s.pairs.length) { alerts.push('<div class="ws-alert">Preview mode is on: nothing is written. Check the planned changes below, then press <b>Apply the preview</b>, or turn preview off in Settings to sync on its own.</div>'); }
            $('#wsAlerts').innerHTML = alerts.join('');

            $('#wsPairs').innerHTML = s.pairs.map(function (p) {
                var head = '<div class="ws-row ws-between"><h3 style="margin:0">' + esc(p.a) + ' ↔ ' + esc(p.b) + (p.enabled ? '' : ' <span class="ws-badge">off</span>') + '</h3>' +
                    '<button is="emby-button" type="button" class="raised" data-run="' + p.index + '"><span>Full sync</span></button></div>';
                var last = p.last ? 'Last ' + esc(p.kind || 'pass') + ' ' + ago(p.last, s.now) + ' (' + p.seconds + 's)' : 'Not synced yet';
                var grid = '<div class="ws-grid" style="margin-top:.8em">' +
                    card('Matched titles', fmtN(p.matched), fmtN(p.checked) + ' checked') +
                    card('Already the same', fmtN(p.in_sync), '') +
                    card(s.dry_run ? 'Changes to make' : 'Written last pass', fmtN(s.dry_run ? p.plan.length : p.applied), '') +
                    card('Not matched', fmtN(p.unmatched.length), 'on the last full pass') + '</div>';
                var err = p.error ? '<div class="ws-alert bad">' + esc(p.error) + '</div>' : '';
                var plan = s.dry_run ? '<h3>Planned changes</h3>' + table(p.plan, [
                    ['Title', function (r) { return esc(r.title); }], ['', function (r) { return esc(r.dir); }],
                    ['Change', function (r) { return esc(r.what); }], ['Played', function (r) { return when(r.when); }]]) : '';
                var recent = '<details' + (s.dry_run ? '' : ' open') + '><summary>Recent changes (' + p.recent.length + ')</summary>' + table(p.recent, [
                    ['When', function (r) { return ago(r.t, s.now); }], ['Title', function (r) { return esc(r.title); }],
                    ['', function (r) { return esc(r.dir); }], ['Change', function (r) { return esc(r.what); }]]) + '</details>';
                var un = '<details><summary>Not matched (' + p.unmatched.length + ')</summary><p class="ws-small ws-muted">Titles watched or in progress on one side that couldn\'t be found on the other, usually because they aren\'t in both libraries or have no TMDB/IMDb/TVDB id.</p>' +
                    table(p.unmatched, [['Title', function (r) { return esc(r.title); }], ['State', function (r) { return esc(r.state); }], ['Why', function (r) { return esc(r.why); }]]) + '</details>';
                return '<div class="ws-card">' + head + '<div class="ws-muted ws-small" style="margin-top:.3em">' + last + '</div>' + err + grid + plan + recent + un + '</div>';
            }).join('');
        }
        function card(title, big, sub) {
            return '<div class="ws-card"><h3>' + title + '</h3><div class="ws-big">' + big + '</div><div class="ws-muted ws-small">' + (sub || '&nbsp;') + '</div></div>';
        }
        view.addEventListener('click', function (e) {
            var b = e.target.closest('[data-run]');
            if (b) { run(true, false, +b.getAttribute('data-run')); }
        });

        function tick() {
            return getJson('watchsync/status').then(renderStatus, function () { $('#wsPill').textContent = 'Not responding'; });
        }

        // ---- Continue Watching -------------------------------------------------------------
        function fillCwPairs() {
            var sel = $('#wsCwPair');
            var cur = sel.value;
            sel.innerHTML = (config && config.Pairs || []).map(function (p, i) {
                return '<option value="' + i + '">' + esc(p.NameA) + ' ↔ ' + esc(p.NameB) + '</option>';
            }).join('');
            if (cur) { sel.value = cur; }
        }
        function cwTable(rows) {
            if (!rows.length) { return '<p class="ws-muted ws-small">Nothing in progress.</p>'; }
            return '<div class="ws-tablewrap"><table><tbody>' + rows.map(function (r, i) {
                return '<tr><td class="num">' + (i + 1) + '</td><td>' + esc(r.title) +
                    '<div class="ws-meter"><i style="width:' + Math.min(100, r.pct) + '%"></i></div>' +
                    (r.issue ? '<div class="ws-issue">⚠ ' + esc(r.issue) + '</div>' : '') +
                    (r.note ? '<div class="ws-small ws-muted">' + esc(r.note) + '</div>' : '') + '</td>' +
                    '<td class="num">' + esc(r.pos) + (r.dur ? ' / ' + esc(r.dur) : '') + '</td>' +
                    '<td class="ws-small ws-muted">' + when(r.last) + '</td></tr>';
            }).join('') + '</tbody></table></div>';
        }
        function loadCompare() {
            var i = $('#wsCwPair').value;
            if (i === '' || i == null) { $('#wsCwBody').innerHTML = '<div class="ws-alert warn">Add a user pair first.</div>'; return; }
            $('#wsCwBody').innerHTML = '<p class="ws-muted">Loading both Continue Watching rows…</p>';
            getJson('watchsync/compare?Pair=' + i).then(function (r) {
                if (r.error) { $('#wsCwBody').innerHTML = '<div class="ws-alert bad">' + esc(r.error) + '</div>'; return; }
                var issues = r.a.concat(r.b).filter(function (x) { return x.issue; }).length;
                var summary = issues ? '<div class="ws-alert warn">' + issues + ' differences between the two rows.</div>'
                                     : '<div class="ws-alert">Both Continue Watching rows match.</div>';
                var next = function (l) {
                    return l.length ? '<details><summary>Next episodes listed (' + l.length + ')</summary><p class="ws-small ws-muted">Plex shows the next episode of shows in progress in Continue Watching; Emby shows them in Next Up.</p><ul class="ws-current">' +
                        l.map(function (t) { return '<li>' + esc(t) + '</li>'; }).join('') + '</ul></details>' : '';
                };
                $('#wsCwBody').innerHTML = summary + '<div class="ws-cols">' +
                    '<div class="ws-card"><h3>' + esc(r.a_label) + '</h3>' + cwTable(r.a) + next(r.a_next) + '</div>' +
                    '<div class="ws-card"><h3>' + esc(r.b_label) + '</h3>' + cwTable(r.b) + next(r.b_next) + '</div></div>';
            }, function () { $('#wsCwBody').innerHTML = '<div class="ws-alert bad">Couldn\'t load the rows.</div>'; });
        }
        $('#wsCwPair').addEventListener('change', loadCompare);
        $('#wsCwRefresh').addEventListener('click', loadCompare);
        $('#wsCwFix').addEventListener('click', function () {
            var i = $('#wsCwPair').value;
            if (i === '') { return; }
            run(true, false, +i);
            setTimeout(loadCompare, 8000);
        });

        // ---- log ---------------------------------------------------------------------------
        function tickLog() {
            return getJson('watchsync/log?After=' + logAfter).then(function (r) {
                var box = $('#wsLog');
                var atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 20;
                r.lines.forEach(function (l) {
                    var div = document.createElement('div');
                    div.className = l[1]; div.textContent = l[2];
                    box.appendChild(div);
                });
                while (box.childNodes.length > 2000) { box.removeChild(box.firstChild); }
                logAfter = r.last;
                if (atEnd) { box.scrollTop = box.scrollHeight; }
            });
        }

        view.addEventListener('viewshow', function () {
            loading.show();
            fetchConfig().then(function (c) {
                fillConfig(c);
                loading.hide();
                // Load the users of sources that are already set up, to label the pairs.
                ['A', 'B'].forEach(function (s) {
                    var kind = c['Source' + s + 'Kind'];
                    if (kind === 'local' || (c['Source' + s + 'Url'] && c['Source' + s + 'Key'])) { testSource(s, false); }
                });
            });
            tick();
            timer = setInterval(function () {
                tick();
                if ($('[data-pane="log"]').classList.contains('on')) { tickLog(); }
            }, 3000);
        });
        view.addEventListener('viewhide', function () { if (timer) { clearInterval(timer); timer = null; } });
    };
});
