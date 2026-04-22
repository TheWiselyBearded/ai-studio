@echo off
echo Starting ComfyUI Main Workspace...
call conda activate comfy_main
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Main
python main.py --enable-manager --port 8000
pause