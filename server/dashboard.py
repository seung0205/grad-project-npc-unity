from fastapi.responses import HTMLResponse
import base64
from datetime import datetime

request_history = []

def add_to_history(img_b64, hm_b64, candidates, audio_samples=[], wav_b64='', person_detected=False, status="Searching"):
    request_history.append({
        "time": datetime.now().strftime("%H:%M:%S"),
        "img": img_b64,
        "heatmap": hm_b64,
        "candidates": candidates,
        "audio": audio_samples,
        "audio_b64": wav_b64,
        "person_detected": person_detected,
        "status": status
    })
    if len(request_history) > 50:
        request_history.pop(0)

DASHBOARD_HTML = """
<!DOCTYPE html>
<html>
<head>
<title>EZ-VSL Dashboard</title>
<style>
* { margin: 0; padding: 0; box-sizing: border-box; }
body { background: #1a1a2e; color: #eee; font-family: monospace; }
h1 { padding: 16px; background: #16213e; border-bottom: 1px solid #0f3460; font-size: 18px; }
#refresh-info { padding: 8px 16px; font-size: 12px; color: #888; background: #16213e; }
#timeline { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.frame { 
    display: flex; align-items: center; gap: 12px;
    background: #16213e; border-radius: 8px; padding: 12px;
    border-left: 3px solid #0f3460;
}
.frame:first-child { border-left-color: #e94560; }
.frame .time { min-width: 70px; font-size: 13px; color: #aaa; }
.frame .index { min-width: 40px; font-size: 12px; color: #666; }
.frame img { border-radius: 4px; border: 1px solid #333; }
.frame .label { font-size: 11px; color: #888; text-align: center; }
.frame .candidates { font-size: 11px; color: #4fc3f7; margin-left: 8px; }
.img-group { text-align: center; }
</style>
<script>
let lastTime = '';
function refresh() {
    fetch('/history_data')
        .then(r => r.json())
        .then(data => {
            if (data.length === 0) return;
            if (data[0].time === lastTime) return; // check newest frame time
            lastTime = data[0].time;
            const tl = document.getElementById('timeline');
            tl.innerHTML = '';
            data.forEach((req, i) => {
                const div = document.createElement('div');
                div.className = 'frame';

                const index = document.createElement('div');
                index.className = 'index';
                index.textContent = '#' + (data.length - i);
                div.appendChild(index);

                const time = document.createElement('div');
                time.className = 'time';
                time.textContent = req.time;
                div.appendChild(time);

                const imgGroup = document.createElement('div');
                imgGroup.className = 'img-group';
                imgGroup.innerHTML = `<img src="data:image/jpeg;base64,${req.img}" width="160" height="160"/><div class="label">Camera</div>`;
                div.appendChild(imgGroup);

                const hmGroup = document.createElement('div');
                hmGroup.className = 'img-group';
                hmGroup.innerHTML = `<img src="data:image/jpeg;base64,${req.heatmap}" width="160" height="160"/><div class="label">Heatmap</div>`;
                div.appendChild(hmGroup);
                // candidates
                const cands = document.createElement('div');
                cands.className = 'candidates';
                cands.textContent = req.candidates.map(c => `(${c.x},${c.y}) s=${c.score.toFixed(2)}`).join(' | ');
                div.appendChild(cands);

                // 🔴 overlap indicator
                const overlapBadge = document.createElement('div');
                overlapBadge.textContent = req.status === 'Found!' ? '🔴 Overlap ✅' : '🔴 Overlap ❌';
                overlapBadge.style.cssText = `
                    font-size:12px;
                    padding:3px 8px;
                    border-radius:4px;
                    margin-left:8px;
                    color:white;
                    background:${req.status === 'Found!' ? '#4caf50' : '#555'};
                `;
                div.appendChild(overlapBadge);
                // NPC movement state badge
                const stateBadge = document.createElement('div');

                const isMoving = req.status === 'Found!';

                stateBadge.textContent = isMoving ? '🚶 Moving' : '🔍 Scanning';

                stateBadge.style.cssText = `
                    font-size:12px;
                    padding:3px 8px;
                    border-radius:4px;
                    margin-left:8px;
                    color:white;
                    background:${isMoving ? '#9c27b0' : '#555'};
                `;

                div.appendChild(stateBadge);

                // person detected indicator
                const personBadge = document.createElement('div');
                personBadge.textContent = req.person_detected ? '👤 ✅' : '👤 ❌';
                personBadge.style.cssText = `font-size:12px;padding:3px 8px;border-radius:4px;margin-left:4px;color:white;background:${req.person_detected ? '#2196f3' : '#555'}`;
                div.appendChild(personBadge);

                const canvas = document.createElement('canvas');
                canvas.width = 160; canvas.height = 60;
                canvas.style = 'border:1px solid #333;border-radius:4px;margin-left:12px;';
                const ctx = canvas.getContext('2d');
                ctx.fillStyle = '#16213e';
                ctx.fillRect(0, 0, 160, 60);
                ctx.strokeStyle = '#4fc3f7';
                ctx.beginPath();
                req.audio.forEach((v, j) => {
                    const x = (j / req.audio.length) * 160;
                    const y = 30 - v * 25;
                    j === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                });
                ctx.stroke();
                div.appendChild(canvas);

                const btn = document.createElement('button');
                btn.textContent = '▶ Play';
                btn.style = 'display:block;margin-top:4px;background:#0f3460;color:#eee;border:none;padding:4px 8px;border-radius:4px;cursor:pointer;font-size:11px;';
                btn.onclick = () => {
                    const audioData = atob(req.audio_b64);
                    const arrayBuffer = new ArrayBuffer(audioData.length);
                    const view = new Uint8Array(arrayBuffer);
                    for (let k = 0; k < audioData.length; k++) view[k] = audioData.charCodeAt(k);
                    const audioCtx = new AudioContext();
                    audioCtx.decodeAudioData(arrayBuffer, buffer => {
                        const source = audioCtx.createBufferSource();
                        source.buffer = buffer;
                        source.connect(audioCtx.destination);
                        source.start();
                    });
                };
                div.appendChild(btn);

                tl.appendChild(div);
            });
            document.getElementById('refresh-info').textContent = 
                `Last updated: ${new Date().toLocaleTimeString()} | Total frames: ${data.length}`;
        });
}
setInterval(refresh, 2000);
refresh();
</script>
</head>
<body>
<h1>EZ-VSL Live Dashboard</h1>
<div id="refresh-info">Loading...</div>
<div id="timeline"></div>
</body>
</html>
"""