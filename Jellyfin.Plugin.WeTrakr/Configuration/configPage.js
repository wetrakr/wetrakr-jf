// WeTrakr plugin config page script.
// Consumes the Plugins/WeTrakr controller exposed by the plugin.
// Lives inside Jellyfin's admin UI, so `ApiClient` / `Dashboard` globals are available.
define([], function () {
    'use strict';

    var PLUGIN_PATH = 'Plugins/WeTrakr';
    var POLL_INTERVAL_MS = 5000;
    var pollTimer = null;

    function $(id) { return document.getElementById(id); }

    function showState(state) {
        ['Disconnected', 'Pairing', 'Connected'].forEach(function (s) {
            var el = $('wtState' + s);
            if (el) el.style.display = (s === state) ? 'block' : 'none';
        });
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function apiUrl(path) {
        return ApiClient.getUrl(PLUGIN_PATH + '/' + path);
    }

    function refreshStatus() {
        return ApiClient.getJSON(apiUrl('Status')).then(function (s) {
            if (s.Connected) {
                $('wtUsername').textContent = s.Username || '(unknown user)';
                var last = s.LastScrobbleAt ? new Date(s.LastScrobbleAt).toLocaleString() : 'never';
                $('wtStats').textContent = s.ScrobbleCount + ' events sent — last at ' + last;
                $('wtTogglePlaying').checked = !!s.ScrobblePlaying;
                $('wtToggleWatched').checked = !!s.ScrobbleWatched;
                $('wtToggleRatings').checked = !!s.ScrobbleRatings;
                showState('Connected');
            } else {
                showState('Disconnected');
            }
        });
    }

    function startPairing() {
        Dashboard.showLoadingMsg();
        return ApiClient.ajax({ type: 'POST', url: apiUrl('ConnectStart') })
            .then(function (res) { return res.json(); })
            .then(function (code) {
                Dashboard.hideLoadingMsg();
                $('wtUserCode').textContent = code.user_code;
                $('wtVerificationLink').href = code.verification_url;
                $('wtVerificationLink').textContent = code.verification_url;
                $('wtPairingStatus').textContent = 'Waiting for confirmation…';
                showState('Pairing');

                pollTimer = setInterval(pollForToken, POLL_INTERVAL_MS);
            })
            .catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert({ message: 'Could not start pairing: ' + (err && err.message ? err.message : err), title: 'WeTrakr' });
            });
    }

    function pollForToken() {
        ApiClient.ajax({ type: 'POST', url: apiUrl('Poll') })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                if (result.Status === 'connected') {
                    stopPolling();
                    refreshStatus();
                } else if (result.Status === 'expired_token') {
                    stopPolling();
                    $('wtPairingStatus').textContent = 'Code expired. Please start again.';
                    setTimeout(function () { showState('Disconnected'); }, 2500);
                } else if (result.Status === 'authorization_pending') {
                    $('wtPairingStatus').textContent = 'Waiting for confirmation…';
                } else if (result.Status === 'no_pending_code') {
                    stopPolling();
                    showState('Disconnected');
                } else {
                    $('wtPairingStatus').textContent = 'Status: ' + result.Status;
                }
            })
            .catch(function () {
                $('wtPairingStatus').textContent = 'Network error. Retrying…';
            });
    }

    function cancelPairing() {
        stopPolling();
        showState('Disconnected');
    }

    function disconnect() {
        if (!confirm('Disconnect WeTrakr? Playback will stop scrobbling until you reconnect.')) return;
        Dashboard.showLoadingMsg();
        ApiClient.ajax({ type: 'POST', url: apiUrl('Disconnect') })
            .then(function () {
                Dashboard.hideLoadingMsg();
                refreshStatus();
            });
    }

    function updateSetting(key, value) {
        var body = {};
        body[key] = value;
        return ApiClient.ajax({
            type: 'POST',
            url: apiUrl('Settings'),
            data: JSON.stringify(body),
            contentType: 'application/json'
        });
    }

    function bindHandlers() {
        $('wtConnectBtn').addEventListener('click', startPairing);
        $('wtCancelPairingBtn').addEventListener('click', cancelPairing);
        $('wtDisconnectBtn').addEventListener('click', disconnect);

        $('wtTogglePlaying').addEventListener('change', function (e) {
            updateSetting('ScrobblePlaying', e.target.checked);
        });
        $('wtToggleWatched').addEventListener('change', function (e) {
            updateSetting('ScrobbleWatched', e.target.checked);
        });
        $('wtToggleRatings').addEventListener('change', function (e) {
            updateSetting('ScrobbleRatings', e.target.checked);
        });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            bindHandlers();
            refreshStatus();
        });

        view.addEventListener('viewhide', stopPolling);
    };
});
