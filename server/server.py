import io
import os
from fastapi.responses import FileResponse
import cv2
import torch
import numpy as np
import librosa
from PIL import Image
from scipy import signal
from fastapi import FastAPI, File, UploadFile
from fastapi.responses import JSONResponse
from torchvision import transforms
import torch.nn as nn
import torch.nn.functional as F
from model import EZVSL
from dashboard import add_to_history, DASHBOARD_HTML, request_history
from fastapi.responses import HTMLResponse
import base64
from ultralytics import YOLO
import csv
from datetime import datetime
from fastapi import Request
from collections import defaultdict

yolo_model = YOLO("yolov8n.pt")  # downloads automatically
LOG_FILE = os.environ.get("LOG_FILE", "results.csv")

USE_YOLO = True # YOLO 쓸지말지
move_stats = {"total_moves": 0, "person_present": 0}
trial_stats = {"moves": 0, "person_present": 0}

app = FastAPI()

def log_result(scene, epoch, found, time_taken, scan_count, move_count=0, valid=True, person_ratio="", correct_moves=0):
    with open(LOG_FILE, 'a', newline='') as f:
        writer = csv.writer(f)
        writer.writerow([
            datetime.now().strftime("%H:%M:%S"),
            scene,
            epoch,
            found,
            time_taken,
            scan_count,
            move_count,
            valid,
            person_ratio,
            correct_moves
        ])
device = torch.device('cpu')

# 모델 로드
audio_visual_model = EZVSL(0.03, 512).to(device)
ckp = torch.load('checkpoints/flickr_10k/best.pth', map_location='cpu')
audio_visual_model.load_state_dict({k.replace('module.', ''): ckp['model'][k] for k in ckp['model']})
audio_visual_model.eval()

from torchvision.models import resnet18
from test import NormReducer, Unsqueeze
object_saliency_model = resnet18(pretrained=True)
object_saliency_model.avgpool = nn.Identity()
object_saliency_model.fc = nn.Sequential(
    nn.Unflatten(1, (512, 7, 7)),
    NormReducer(dim=1),
    Unsqueeze(1)
)
object_saliency_model = object_saliency_model.to(device)
object_saliency_model.eval()

image_transform = transforms.Compose([
    transforms.Resize((224, 224), Image.BICUBIC),
    transforms.ToTensor(),
    transforms.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225])
])
audio_transform = transforms.Compose([
    transforms.ToTensor(),
    transforms.Normalize(mean=[0.0], std=[12.0])
])

def extract_candidates(heatmap, top_n=3):
    hm = (heatmap * 255).astype(np.uint8)
    _, thresh = cv2.threshold(hm, 180, 255, cv2.THRESH_BINARY)
    contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []
    for cnt in contours:
        M = cv2.moments(cnt)
        if M["m00"] == 0:
            continue
        cx = int(M["m10"] / M["m00"])
        cy = int(M["m01"] / M["m00"])
        score = float(heatmap[cy, cx])
        candidates.append({"x": cx, "y": cy, "score": score})
    candidates.sort(key=lambda c: c["score"], reverse=True)
    return candidates[:top_n]



@app.post("/analyze")
async def analyze(image: UploadFile = File(...), audio: UploadFile = File(...)):
    # 이미지 처리
    img_bytes = await image.read()
    wav_bytes = await audio.read()

    img = Image.open(io.BytesIO(img_bytes)).convert('RGB')
    img_tensor = image_transform(img).unsqueeze(0).to(device)

    # YOLO check
    person_detected = False
    person_boxes = []

    # always run YOLO for logging
    results = yolo_model(img, verbose=False)
    person_detected_yolo = False

    if results[0].boxes is not None:
        for box in results[0].boxes:
            if int(box.cls[0]) == 0:
                person_detected_yolo = True
                x1, y1, x2, y2 = box.xyxy[0].tolist()
                person_boxes.append((int(x1), int(y1), int(x2), int(y2)))

    if USE_YOLO:
        person_detected = person_detected_yolo
    else:
        person_detected = False
    print(f"YOLO person_detected: {person_detected}")

    # 오디오 처리
    audio_np, sr = librosa.load(io.BytesIO(wav_bytes), sr=None, mono=True)
    print(f"Audio received: duration={len(audio_np)/sr:.2f}s, sr={sr}, max_amplitude={np.max(np.abs(audio_np)):.4f}")

    dur = 3.0
    if audio_np.shape[0] < sr * dur:
        n = int(sr * dur / audio_np.shape[0]) + 1
        audio_np = np.tile(audio_np, n)
    audio_np = audio_np[:int(sr * dur)]
    _, _, spec = signal.spectrogram(audio_np, sr, nperseg=512, noverlap=274)
    spec = np.log(spec + 1e-7)
    spec_tensor = audio_transform(spec).unsqueeze(0).to(device)

    # 추론
    with torch.no_grad():
        heatmap_av = audio_visual_model(img_tensor.float(), spec_tensor.float())[1].unsqueeze(1)
        heatmap_av = F.interpolate(heatmap_av, size=(224, 224), mode='bilinear', align_corners=True)
        heatmap_av = heatmap_av.squeeze().cpu().numpy()

    heatmap_av = (heatmap_av - heatmap_av.min()) / (heatmap_av.max() - heatmap_av.min())

    # check distance between heatmap center and person box center
    heatmap_and_person = False
    min_distance_threshold = 70  # pixels in 224x224 image space

    if person_detected and person_boxes:
        hm = (heatmap_av * 255).astype(np.uint8)
        _, thresh = cv2.threshold(hm, 180, 255, cv2.THRESH_BINARY)

        M = cv2.moments(thresh)

        if M["m00"] > 0:
            hm_cx = int(M["m10"] / M["m00"])
            hm_cy = int(M["m01"] / M["m00"])

            scale_x = 224 / img.width
            scale_y = 224 / img.height

            for (x1, y1, x2, y2) in person_boxes:
                px = int((x1 + x2) / 2 * scale_x)
                py = int((y1 + y2) / 2 * scale_y)

                dist = ((hm_cx - px) ** 2 + (hm_cy - py) ** 2) ** 0.5

                print(
                    f"Heatmap center: ({hm_cx},{hm_cy}) "
                    f"Person center: ({px},{py}) "
                    f"dist: {dist:.1f}"
                )

                if dist < min_distance_threshold:
                    heatmap_and_person = True
                    break

    print(f"heatmap_distance_match: {heatmap_and_person}")
    # save heatmap overlaid on original image
    heatmap_img = np.uint8(heatmap_av * 255)
    heatmap_color = cv2.applyColorMap(heatmap_img, cv2.COLORMAP_JET)

    # save received image too
    img_cv = cv2.cvtColor(np.array(img), cv2.COLOR_RGB2BGR)
    img_resized = cv2.resize(img_cv, (224, 224))
    fin = cv2.addWeighted(heatmap_color, 0.6, img_resized, 0.4, 0)
    if os.environ.get("SAVE_DEBUG_HEATMAP"):
        cv2.imwrite('debug_heatmap.jpg', fin)
    #cv2.imwrite('debug_heatmap.jpg', fin)

    # 카메라 이미지 base64 인코딩
    _, img_encoded = cv2.imencode('.jpg', img_resized)
    img_b64 = base64.b64encode(img_encoded).decode('utf-8')

    # heatmap base64 인코딩
    _, hm_encoded = cv2.imencode('.jpg', fin)
    hm_b64 = base64.b64encode(hm_encoded).decode('utf-8')
    wav_b64 = base64.b64encode(wav_bytes).decode('utf-8')

    audio_normalized = (audio_np / np.max(np.abs(audio_np) + 1e-8))
    audio_samples = audio_normalized[::len(audio_normalized)//200].tolist()
    
    candidates = extract_candidates(heatmap_av)

    # sort candidates by distance to person box center
    if person_detected and person_boxes and candidates:
        scale_x = 224 / img.width
        scale_y = 224 / img.height
        px = int((person_boxes[0][0] + person_boxes[0][2]) / 2 * scale_x)
        py = int((person_boxes[0][1] + person_boxes[0][3]) / 2 * scale_y)
        candidates.sort(key=lambda c: ((c["x"]-px)**2 + (c["y"]-py)**2)**0.5)
    
    status = "Searching"
    
    if USE_YOLO:
        if person_detected:
            status = "Found!"
        else:
            status = "Person ❌"
    else:
        status = "Found!" if any(c["score"] > 0.85 for c in candidates) else "Searching"
        person_detected = False
        if status == "Found!":
            move_stats["total_moves"] += 1
            trial_stats["moves"] += 1 
            if person_detected_yolo:
                move_stats["person_present"] += 1
                trial_stats["person_present"] += 1

    wav_b64 = base64.b64encode(wav_bytes).decode('utf-8')
    add_to_history(img_b64, hm_b64, candidates, audio_samples, wav_b64, person_detected, status)

    return JSONResponse({"candidates": candidates, "person_detected": person_detected, "status": status})

@app.get("/heatmap", response_class=HTMLResponse)
async def get_heatmap():
    return DASHBOARD_HTML

@app.get("/history_data")
async def history_data():
    from dashboard import request_history as dash_history
    return list(reversed(dash_history))

@app.get("/results", response_class=HTMLResponse)
async def results_page():
    scenes = defaultdict(list)

    if os.path.exists(LOG_FILE):
        with open(LOG_FILE, 'r') as f:
            for row in csv.reader(f):
                if len(row) < 8:
                    continue
                scenes[row[1]].append(row)

    html = ""
    ratio = move_stats["person_present"] / move_stats["total_moves"] * 100 if move_stats["total_moves"] > 0 else 0

    for scene, rows in scenes.items():
        total = len(rows)
        found = sum(1 for r in rows if "true" in r[3].lower())
        avg_time = sum(float(r[4]) for r in rows) / total if total else 0
        avg_scans = sum(int(r[5]) for r in rows) / total if total else 0
        valid = [r for r in rows if "true" in r[7].lower() and int(r[5]) > 1]

        valid_total = len(valid)
        valid_found = sum(1 for r in valid if "true" in r[3].lower())
        valid_avg_time = sum(float(r[4]) for r in valid) / valid_total if valid_total else 0
        valid_avg_scans = sum(int(r[5]) for r in valid) / valid_total if valid_total else 0
        valid_avg_moves = sum(int(r[6]) for r in valid) / valid_total if valid_total else 0
        valid_success_rate = 100 * valid_found / valid_total if valid_total else 0
        success_rate = 100 * found / total if total else 0

        html += f"<h2>{scene}</h2><table>"
        html += "<tr><th>Time</th><th>Trial</th><th>Found</th><th>Time(s)</th><th>Scans</th><th>Moves</th><th>Correct Moves</th><th>Person/Move</th></tr>"

        avg_moves = sum(int(r[6]) for r in rows) / total if total else 0

        for r in rows:
            icon = "✅" if "true" in r[3].lower() else "❌"
            person_ratio = r[8] if len(r) > 8 and r[8] else "-"
            correct_moves = r[9] if len(r) > 9 else "-"
            html += f"<tr><td>{r[0]}</td><td>{r[2]}</td><td>{icon}</td><td>{r[4]}s</td><td>{r[5]}</td><td>{r[6]}</td><td>{correct_moves}</td><td>{person_ratio}</td></tr>"



        html += f"""
        <tr style="background:#0f3460;font-weight:bold;">
            <td>Average</td>
            <td>{total} trials</td>
            <td>{found}/{total} ({success_rate:.1f}%)</td>
            <td>{avg_time:.1f}s</td>
            <td>{avg_scans:.1f}</td>
            <td>{avg_moves:.1f}</td>
            <td>-</td>
            <td>-</td>
        </tr>
        """
        html += f"""
        <tr style="background:#533483;font-weight:bold;">
            <td>Avg (scans≥2)</td>
            <td>{valid_total} trials</td>
            <td>{valid_found}/{valid_total} ({valid_success_rate:.1f}%)</td>
            <td>{valid_avg_time:.1f}s</td>
            <td>{valid_avg_scans:.1f}</td>
            <td>{valid_avg_moves:.1f}</td>
            <td>-</td>
            <td>-</td>
        </tr>
        """
        html += f"""
        <tr style="background:#1a6b3c;font-weight:bold;">
            <td>Person/Move Total</td>
            <td colspan="5">{move_stats["person_present"]}/{move_stats["total_moves"]} moves had person detected ({ratio:.1f}%)</td>
        </tr>
        """

        html += "</table><br>"

    return f"""
    <html>
    <head>
        <title>Results</title>
        <meta http-equiv="refresh" content="3">
        <style>
            body {{ background:#1a1a2e; color:#eee; font-family:monospace; padding:20px; }}
            table {{ border-collapse:collapse; width:70%; margin-bottom:8px; }}
            th, td {{ border:1px solid #333; padding:8px 12px; text-align:center; }}
            th {{ background:#16213e; }}
            h2 {{ margin:16px 0 8px; color:#4fc3f7; }}
            tr:nth-child(even) {{ background:#16213e; }}
        </style>
    </head>
    <body>
        <h1>Results by Scene</h1>

        <button onclick="resetResults()" style="
            background:#e94560;
            color:white;
            border:none;
            padding:8px 16px;
            border-radius:4px;
            cursor:pointer;
            margin-bottom:16px;
            font-family:monospace;">
            🗑 Reset Results
        </button>

        <script>
        function resetResults() {{
            fetch('/reset_results', {{method: 'POST'}})
                .then(() => location.reload());
        }}
        </script>
        {html}
    </body>
    </html>
    """

@app.post("/log_epoch")
@app.post("/log_epoch/")
async def log_epoch(data: dict):
    ratio_str = f"{trial_stats['person_present']}/{trial_stats['moves']}"
    correct_moves = data.get("correct_moves", 0)
    log_result(data["scene"], data["epoch"], data["found"], data["time"],
                data["scans"], data.get("moves", 0), data.get("valid", True), ratio_str, correct_moves)
    trial_stats["moves"] = 0
    trial_stats["person_present"] = 0
    return {"ok": True}

@app.post("/reset_results")
async def reset_results():
    if os.path.exists(LOG_FILE):
        open(LOG_FILE, 'w').close()
    move_stats["total_moves"] = 0
    move_stats["person_present"] = 0
    trial_stats["moves"] = 0
    trial_stats["person_present"] = 0
    return {"ok": True}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)