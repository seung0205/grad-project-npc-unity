# NPC 시청각 추적 — 추론 서버

Unity 클라이언트가 보낸 프레임과 오디오를 받아
소리 위치 히트맵과 사람 검출 결과를 조합해 후보 좌표를 반환합니다.

- `server.py` — FastAPI 추론 서버, 실험 집계 엔드포인트
- `dashboard.py` — 히트맵·오디오 파형 실시간 확인 대시보드
- `convert.py` — 체크포인트 변환
- `results.csv` — 5개 환경 500회 측정 결과

음원 위치 추정 모델은 EZ-VSL 원본(https://github.com/stoneMo/EZ-VSL)을 사용했으며,
이 폴더는 클라이언트와 통신하는 추론 서버와 실험 집계 스크립트입니다.
사람 검출은 YOLOv8(ultralytics)을 사용합니다.