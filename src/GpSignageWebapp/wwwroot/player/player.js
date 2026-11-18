// Signage player: connects to the SignalR hub, joins a display group,
// then cycles through the display's playlist (crossfade between images,
// videos play to their end). Runs in a kiosk browser on the Pi.
//
// URL: https://<host>/player/?display=<slug>

(() => {
    "use strict";

    // ---- Config ---------------------------------------------------------------
    const HEARTBEAT_MS = 30_000;          // report liveness to the server
    const STATUS_HIDE_MS = 4_000;         // auto-hide the status pill
    const DEFAULT_DURATION_S = 10;        // fallback if a slide has no duration
    const CROSSFADE_MS = 600;             // must match CSS .media-img transition

    // ---- DOM ------------------------------------------------------------------
    const idleEl = document.getElementById("idle");
    const imgA = document.getElementById("imgA");
    const imgB = document.getElementById("imgB");
    const videoEl = document.getElementById("mediaVideo");
    const statusEl = document.getElementById("status");

    // ---- State ----------------------------------------------------------------
    const params = new URLSearchParams(location.search);
    const slug = (params.get("display") || "").trim().toLowerCase();

    let playlist = [];          // array of slide objects
    let index = 0;              // current position in playlist
    let frontLayer = imgA;      // which <img> is currently visible
    let backLayer = imgB;
    let advanceTimer = null;    // setTimeout handle for image dwell
    let heartbeatTimer = null;
    let rotationToken = 0;      // invalidates stale timers when playlist changes

    // ---- Status pill ----------------------------------------------------------
    let statusHideTimer = null;
    function setStatus(text, isError) {
        statusEl.textContent = text;
        statusEl.classList.add("show");
        statusEl.classList.toggle("err", !!isError);
        if (statusHideTimer) clearTimeout(statusHideTimer);
        if (!isError) {
            statusHideTimer = setTimeout(() => statusEl.classList.remove("show"), STATUS_HIDE_MS);
        }
    }

    // ---- Helpers --------------------------------------------------------------
    function absolute(url) {
        if (!url) return url;
        return url.startsWith("http") || url.startsWith("/") ? url : "/" + url;
    }

    function isVideo(slide) {
        return (slide.mediaType || "").toLowerCase() === "video";
    }

    // ---- Rendering ------------------------------------------------------------
    function showIdle(message) {
        stopRotation();
        playlist = [];
        imgA.classList.remove("visible");
        imgB.classList.remove("visible");
        videoEl.hidden = true;
        videoEl.pause();
        videoEl.removeAttribute("src");
        idleEl.style.display = "block";
        if (message) idleEl.textContent = message;
    }

    // Crossfade to an image slide using the two stacked layers.
    function showImage(slide, token) {
        if (token !== rotationToken) return;

        const nextUrl = absolute(slide.mediaUrl);

        // Load into the back layer, then swap visibility on load.
        backLayer.onload = () => {
            if (token !== rotationToken) return;
            backLayer.classList.add("visible");
            frontLayer.classList.remove("visible");
            const t = frontLayer; frontLayer = backLayer; backLayer = t;   // swap roles
            idleEl.style.display = "none";
            videoEl.hidden = true;
            scheduleAdvance(durationMs(slide), token);
        };
        backLayer.onerror = () => {
            if (token !== rotationToken) return;
            console.warn("[player] image failed to load:", nextUrl);
            scheduleAdvance(1500, token);   // skip broken slide after a short delay
        };
        backLayer.src = nextUrl;
        // If already cached, onload may not fire reliably.
        if (backLayer.complete && backLayer.naturalWidth > 0) {
            backLayer.onload();
        }
    }

    // Play a video slide; advance when it ends (or on error).
    function showVideo(slide, token) {
        if (token !== rotationToken) return;

        imgA.classList.remove("visible");
        imgB.classList.remove("visible");
        idleEl.style.display = "none";

        videoEl.hidden = false;
        videoEl.src = absolute(slide.mediaUrl);
        videoEl.onended = () => { if (token === rotationToken) advance(token); };
        videoEl.onerror = () => {
            if (token !== rotationToken) return;
            console.warn("[player] video failed:", videoEl.src);
            scheduleAdvance(1500, token);
        };
        videoEl.load();
        videoEl.play().catch(() => {
            // Autoplay blocked; fall back to a timed advance so we don't get stuck.
            scheduleAdvance(durationMs(slide), token);
        });
    }

    function durationMs(slide) {
        const s = Number(slide && slide.durationSeconds);
        return (Number.isFinite(s) && s > 0 ? s : DEFAULT_DURATION_S) * 1000;
    }

    function scheduleAdvance(ms, token) {
        clearTimeout(advanceTimer);
        advanceTimer = setTimeout(() => advance(token), ms);
    }

    function showSlide(slide, token) {
        if (!slide || !slide.mediaUrl) { advance(token); return; }
        if (isVideo(slide)) showVideo(slide, token);
        else showImage(slide, token);
    }

    function advance(token) {
        if (token !== rotationToken) return;
        if (!playlist.length) { showIdle("Waiting for content…"); return; }
        index = (index + 1) % playlist.length;
        showSlide(playlist[index], token);
    }

    function stopRotation() {
        rotationToken++;          // invalidate any pending callbacks
        clearTimeout(advanceTimer);
        advanceTimer = null;
    }

    // Start (or restart) rotation from the first slide.
    function playPlaylist(slides) {
        stopRotation();
        const token = rotationToken;

        playlist = Array.isArray(slides) ? slides.filter(s => s && s.mediaUrl) : [];
        if (!playlist.length) { showIdle("Waiting for content…"); return; }

        index = 0;
        idleEl.style.display = "none";
        showSlide(playlist[0], token);
    }

    // ---- Fatal states ---------------------------------------------------------
    function showFatal(message) {
        setStatus(message, true);
        showIdle(message);
        idleEl.style.color = "#b00";
    }

    // ---- SignalR --------------------------------------------------------------
    function startHeartbeat(connection) {
        if (heartbeatTimer) clearInterval(heartbeatTimer);
        heartbeatTimer = setInterval(() => {
            if (connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("Heartbeat", slug).catch(() => { /* ignore */ });
            }
        }, HEARTBEAT_MS);
    }

    async function start() {
        if (!slug) {
            showFatal("Missing ?display=<slug> in the URL.");
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/signage")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        window.__hub = connection;   // for manual inspection in DevTools

        // Server -> client: new playlist (sent on join and whenever admin changes it).
        connection.on("ShowPlaylist", (payload) => {
            console.log("[ShowPlaylist] received:", payload);
            playPlaylist(payload && payload.slides ? payload.slides : []);
        });

        // Backward-compat: a single slide push behaves like a 1-item playlist.
        connection.on("ShowSlide", (slide) => {
            console.log("[ShowSlide] received:", slide);
            playPlaylist(slide ? [slide] : []);
        });

        // Admin renamed the display's slug — rejoin under the new slug.
        connection.on("SlugChanged", async (newSlug) => {
            console.log("[SlugChanged] new slug:", newSlug);
            if (!newSlug) return;
            // Update the URL so a manual reload keeps working, then rejoin.
            const url = new URL(location.href);
            url.searchParams.set("display", newSlug);
            history.replaceState(null, "", url);
            playPlaylist([]);            // clear until the server sends the new playlist
            try {
                await connection.invoke("JoinDisplay", newSlug);
                setStatus(`Renamed to "${newSlug}"`);
            } catch (e) { console.error("Rejoin after rename failed", e); }
        });

        // Admin deleted this display — go idle with a message.
        connection.on("DisplayDeleted", () => {
            console.log("[DisplayDeleted]");
            showIdle("This display was deleted.");
        });

        connection.onreconnecting(() => setStatus("Reconnecting…", true));
        connection.onreconnected(async () => {
            setStatus("Connected");
            try { await joinDisplay(connection); }
            catch (e) { console.error("Re-join failed", e); }
        });
        connection.onclose(async () => {
            setStatus("Disconnected — retrying…", true);
            await new Promise(r => setTimeout(r, 5000));
            start();
        });

        async function joinDisplay(conn) {
            setStatus("Connected");
            await conn.invoke("JoinDisplay", slug);
            console.log("[JoinDisplay] joined:", slug);
        }

        try {
            await connection.start();
            await joinDisplay(connection);
            startHeartbeat(connection);
        } catch (err) {
            const msg = (err && err.message) ? err.message : String(err);
            console.error("SignalR start/join failed:", err);
            if (msg.includes("Unknown display")) {
                showFatal(`Unknown display "${slug}". Check the URL slug.`);
            } else {
                setStatus("Cannot connect — retrying…", true);
                setTimeout(start, 5000);
            }
        }
    }

    window.addEventListener("load", start);
})();
