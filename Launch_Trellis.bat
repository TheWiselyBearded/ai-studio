@echo off
echo Starting Trellis2 3D Generation...
call conda activate comfy_trellis
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Trellis
python main.py --port 8002
pause
