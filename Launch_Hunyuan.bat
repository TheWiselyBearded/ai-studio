@echo off
echo Starting Hunyuan Quarantine Zone...
call conda activate comfy_hunyuan
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Hunyuan
python main.py --port 8001
pause