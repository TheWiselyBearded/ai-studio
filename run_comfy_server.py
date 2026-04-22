import json
import urllib.request
import urllib.parse
import uuid
import websocket  # pip install websocket-client
import argparse
import os
import shutil
import traceback
import subprocess
import threading
import atexit
import re
import base64
import time
from flask import Flask, request, jsonify, send_file  # pip install Flask
from dotenv import load_dotenv  # pip install python-dotenv

# --- Configuration ---
# Each ComfyUI environment runs on its own port:
#   ComfyUI_Hunyuan (3D)  → 8001  (Launch_Hunyuan.bat)
#   ComfyUI_Main   (audio) → 8000  (Launch_Main.bat)
server_address_3d = "127.0.0.1:8001"
server_address_audio = "127.0.0.1:8000"
server_address = server_address_3d  # default for shared helpers
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
RESULTS_DIR = os.path.join(SCRIPT_DIR, "results")

# --- 3D Workflow ---
WORKFLOW_3D_FILE = "Scratch_Text3DPBR.json"
WORKFLOW_3D_OUTPUT_NODE = "27"  # Preview3D — used to detect completion and get filename

# --- Image Workflow ---
WORKFLOW_IMAGE_FILE = "scratch_ZImageTurbo.json"
WORKFLOW_IMAGE_OUTPUT_NODE = "9"  # SaveImage node

# --- Voice Clone Workflows ---
WORKFLOW_VOICE_CLONE_FILE = "scratch_VoiceCloning.json"
WORKFLOW_VOICE_CLONE_SPEECH_FILE = "scratch_VoiceCloneSpeech.json"
WORKFLOW_VOICE_CLONE_SPEECH_OUTPUT_NODE = "11"  # SaveAudio node

# --- Video Workflow ---
WORKFLOW_VIDEO_WAN2_FILE = "scratch_wan2_Video.json"
WORKFLOW_VIDEO_WAN2_OUTPUT_NODE = "108"  # SaveVideo node

# --- Cloud API Keys (loaded from .env) ---
load_dotenv(os.path.join(SCRIPT_DIR, ".env"))
GEMINI_API_KEY = os.environ.get("GEMINI_API_KEY")
RUNWAYML_API_SECRET = os.environ.get("RUNWAYML_API_SECRET")

# The InPaint node saves the textured GLB directly to disk, bypassing ComfyUI's
# /view API. Since both run on the same machine, we read the file from here.
COMFYUI_HUNYUAN_DIR = r"C:\Users\abahrema\Documents\Tools\ComfyUI_Hunyuan"
COMFYUI_MAIN_DIR = r"C:\Users\abahrema\Documents\Tools\ComfyUI_Main"


# =============================================================================
# ComfyUI Helper Functions
# =============================================================================

def queue_prompt(prompt_workflow, client_id, target=None):
    addr = target or server_address
    p = {"prompt": prompt_workflow, "client_id": client_id}
    data = json.dumps(p).encode("utf-8")
    req = urllib.request.Request(f"http://{addr}/prompt", data=data)
    response = urllib.request.urlopen(req)
    return json.loads(response.read())


def get_history(prompt_id, target=None):
    addr = target or server_address
    with urllib.request.urlopen(f"http://{addr}/history/{prompt_id}") as response:
        return json.loads(response.read())


def get_file(filename, subfolder, folder_type, target=None):
    addr = target or server_address
    data = {"filename": filename, "subfolder": subfolder, "type": folder_type}
    url_values = urllib.parse.urlencode(data)
    with urllib.request.urlopen(f"http://{addr}/view?{url_values}") as response:
        return response.read()


def upload_image(filepath, target=None):
    """Upload an image to ComfyUI and return the reference name."""
    addr = target or server_address
    filename = os.path.basename(filepath)
    with open(filepath, "rb") as f:
        file_data = f.read()

    boundary = uuid.uuid4().hex
    body = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="image"; filename="{filename}"\r\n'
        f"Content-Type: application/octet-stream\r\n\r\n"
    ).encode("utf-8") + file_data + f"\r\n--{boundary}--\r\n".encode("utf-8")

    req = urllib.request.Request(
        f"http://{addr}/upload/image",
        data=body,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    resp = urllib.request.urlopen(req)
    result = json.loads(resp.read())
    return result["name"]


def run_comfyui_workflow(workflow, target=None):
    """Queue a workflow, wait for completion via websocket, return the history."""
    addr = target or server_address
    client_id = str(uuid.uuid4())

    print(f"Connecting to ComfyUI at {addr}...")
    ws = websocket.WebSocket()
    ws.connect(f"ws://{addr}/ws?clientId={client_id}")

    print("Queueing prompt...")
    queue_response = queue_prompt(workflow, client_id, target=addr)
    prompt_id = queue_response["prompt_id"]
    print(f"Prompt queued! ID: {prompt_id}")

    print("Waiting for ComfyUI to finish...")
    while True:
        out = ws.recv()
        if isinstance(out, str):
            message = json.loads(out)
            if message["type"] == "executing":
                data = message["data"]
                if data["node"] is None and data["prompt_id"] == prompt_id:
                    print("Execution complete!")
                    break
            elif message["type"] == "execution_error":
                data = message["data"]
                if data.get("prompt_id") == prompt_id:
                    node_id = data.get("node_id", "?")
                    node_type = data.get("node_type", "?")
                    error_msg = data.get("exception_message", "Unknown error")
                    ws.close()
                    raise Exception(
                        f"ComfyUI execution error in node {node_id} ({node_type}): {error_msg}"
                    )
    ws.close()

    return get_history(prompt_id, target=addr)[prompt_id]


def retrieve_output_file(history, output_node_id):
    """Extract file info from a ComfyUI history output node."""
    if output_node_id not in history.get("outputs", {}):
        status = history.get("status", {})
        if status.get("status_str") == "error":
            messages = status.get("messages", [])
            error_msgs = [m for m in messages if "error" in str(m).lower()]
            raise Exception(
                f"ComfyUI execution failed. Status: {status.get('status_str')}. "
                f"Messages: {error_msgs or messages}"
            )
        available = list(history.get("outputs", {}).keys())
        raise Exception(
            f"No output found from node {output_node_id}. "
            f"Available output nodes: {available}. "
            f"Check ComfyUI logs for errors during execution."
        )

    output_data = history["outputs"][output_node_id]

    # Try common output keys
    file_info = None
    for key in ("mesh", "gltf", "glb", "3d", "model", "file", "model_file", "audio", "video", "result"):
        if key in output_data:
            val = output_data[key]
            if isinstance(val, list) and len(val) > 0:
                file_info = val[0]
                break

    # Fallback: grab first list-of-dicts value
    if file_info is None:
        for v in output_data.values():
            if isinstance(v, list) and len(v) > 0 and isinstance(v[0], dict):
                file_info = v[0]
                break

    if file_info is None:
        raise Exception(
            f"Could not find file in node {output_node_id} output. "
            f"Raw output keys: {list(output_data.keys())}. "
            f"Raw output: {json.dumps(output_data, indent=2)[:500]}"
        )

    # Preview3D returns ["filename.glb", null, null] — normalize to dict
    if isinstance(file_info, str):
        file_info = {"filename": file_info, "subfolder": "", "type": "output"}

    return file_info


def download_output(file_info, target=None):
    """Download a file from ComfyUI given its file_info dict, save to results dir."""
    os.makedirs(RESULTS_DIR, exist_ok=True)

    filename = file_info["filename"]
    subfolder = file_info.get("subfolder", "")
    folder_type = file_info.get("type", "output")

    print(f"Downloading {filename} from ComfyUI...")
    file_bytes = get_file(filename, subfolder, folder_type, target=target)

    clean_name = os.path.basename(filename)
    output_path = os.path.join(RESULTS_DIR, clean_name)
    with open(output_path, "wb") as f:
        f.write(file_bytes)

    print(f"Success! Saved to: {output_path}")
    return output_path


# =============================================================================
# 3D Generation
# =============================================================================

def ensure_rgb_image(image_path):
    """Ensure image is RGB JPG. Converts PNGs and grayscale/palette images."""
    from PIL import Image

    img = Image.open(image_path)
    ext = os.path.splitext(image_path)[1].lower()

    if img.mode == "RGB" and ext in (".jpg", ".jpeg"):
        return image_path  # already good

    print(f"[3D] Converting image (mode={img.mode}, fmt={ext}) to RGB JPG...")
    img = img.convert("RGB")
    converted_path = os.path.splitext(image_path)[0] + "_converted.jpg"
    img.save(converted_path, "JPEG", quality=95)
    return converted_path


def generate_3d(image_path):
    """Run the Hunyuan3D 2.1 PBR pipeline on a single image and return the saved GLB path."""
    target = server_address_3d
    image_path = ensure_rgb_image(image_path)

    with open(os.path.join(SCRIPT_DIR, WORKFLOW_3D_FILE), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    # Get the mesh name from the workflow so we know what file to look for
    mesh_name = workflow["49"]["inputs"]["output_mesh_name"]

    print(f"[3D] Uploading {os.path.basename(image_path)}... (ComfyUI @ {target})")
    uploaded_name = upload_image(image_path, target=target)
    workflow["14"]["inputs"]["image"] = uploaded_name

    # Run workflow — node 27 (Preview3D) signals completion
    run_comfyui_workflow(workflow, target=target)

    # The InPaint node saves the textured GLB to ComfyUI's temp/ directory.
    glb_filename = f"{mesh_name}.glb"
    glb_path = os.path.join(COMFYUI_HUNYUAN_DIR, "temp", glb_filename)

    if not os.path.isfile(glb_path):
        # Fallback: check output/ too
        glb_path = os.path.join(COMFYUI_HUNYUAN_DIR, "output", glb_filename)

    if not os.path.isfile(glb_path):
        raise FileNotFoundError(
            f"Textured GLB not found. Searched temp/ and output/ for {glb_filename}."
        )

    # Copy to our results dir
    os.makedirs(RESULTS_DIR, exist_ok=True)
    output_path = os.path.join(RESULTS_DIR, glb_filename)
    shutil.copy2(glb_path, output_path)
    print(f"[3D] Success! Textured PBR mesh saved to: {output_path}")
    return output_path


# =============================================================================
# Audio Generation
# =============================================================================

def generate_audio(text_input, character="Female", style="Warm"):
    """Run the QwenTTS voice pipeline and return the path to the saved audio file."""
    target = server_address_audio

    with open(os.path.join(SCRIPT_DIR, "Scratch_Voice_design.json"), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    # Inject text
    workflow["15"]["inputs"]["text"] = text_input
    # Inject voice character and style
    workflow["10"]["inputs"]["character"] = character
    workflow["10"]["inputs"]["style"] = style

    print(f"[Audio] Generating speech for: '{text_input[:50]}...' (ComfyUI @ {target})")

    history = run_comfyui_workflow(workflow, target=target)
    file_info = retrieve_output_file(history, "11")
    return download_output(file_info, target=target)


# =============================================================================
# Voice Cloning
# =============================================================================

def generate_voice_clone(audio_path, voice_name="voice_1", language="zh"):
    """Clone a voice from reference audio. Creates a .pt voice file in ComfyUI."""
    target = server_address_audio

    with open(os.path.join(SCRIPT_DIR, WORKFLOW_VOICE_CLONE_FILE), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    print(f"[VoiceClone] Uploading {os.path.basename(audio_path)}... (ComfyUI @ {target})")
    uploaded_name = upload_image(audio_path, target=target)

    workflow["8"]["inputs"]["audio"] = uploaded_name
    workflow["7"]["inputs"]["voice_name"] = voice_name
    workflow["6"]["inputs"]["language"] = language

    print(f"[VoiceClone] Cloning voice as '{voice_name}'...")
    run_comfyui_workflow(workflow, target=target)

    # Verify the .pt file was created
    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    pt_path = os.path.join(voices_dir, f"{voice_name}.pt")

    if not os.path.isfile(pt_path):
        raise FileNotFoundError(
            f"Voice file not found at {pt_path}. "
            f"Check ComfyUI logs for errors."
        )

    # Save a copy of the reference audio alongside the .pt file so we can
    # reuse it later without requiring the agent to re-upload it.
    ref_ext = os.path.splitext(audio_path)[1] or ".wav"
    ref_path = os.path.join(voices_dir, f"{voice_name}_ref{ref_ext}")
    shutil.copy2(audio_path, ref_path)
    print(f"[VoiceClone] Reference audio saved to: {ref_path}")

    print(f"[VoiceClone] Success! Voice saved to: {pt_path}")
    return pt_path


def generate_voice_clone_speech(text, voice_name, audio_path,
                                 character="Female", style="Warm", seed=None):
    """Generate speech using a cloned voice and reference audio."""
    import random
    target = server_address_audio

    with open(os.path.join(SCRIPT_DIR, WORKFLOW_VOICE_CLONE_SPEECH_FILE), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    print(f"[VoiceCloneSpeech] Uploading reference audio...")
    uploaded_name = upload_image(audio_path, target=target)

    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    pt_path = os.path.join(voices_dir, f"{voice_name}.pt")

    if not os.path.isfile(pt_path):
        raise FileNotFoundError(f"Voice '{voice_name}' not found at {pt_path}")

    workflow["20"]["inputs"]["audio"] = uploaded_name
    workflow["17"]["inputs"]["voice_name"] = f"{voice_name}.pt"
    workflow["17"]["inputs"]["custom_path"] = pt_path
    workflow["19"]["inputs"]["target_text"] = text
    workflow["19"]["inputs"]["seed"] = seed if seed is not None else random.randint(0, 2**53)
    workflow["10"]["inputs"]["character"] = character
    workflow["10"]["inputs"]["style"] = style

    print(f"[VoiceCloneSpeech] Generating speech: '{text[:50]}...' (voice={voice_name})")

    history = run_comfyui_workflow(workflow, target=target)
    file_info = retrieve_output_file(history, WORKFLOW_VOICE_CLONE_SPEECH_OUTPUT_NODE)
    return download_output(file_info, target=target)


def _find_reference_audio(voice_name):
    """Find the stored reference audio for a cloned voice."""
    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    for ext in (".wav", ".flac", ".mp3", ".ogg"):
        ref_path = os.path.join(voices_dir, f"{voice_name}_ref{ext}")
        if os.path.isfile(ref_path):
            return ref_path
    return None


def list_cloned_voices():
    """Return a list of available cloned voice names (without .pt extension)."""
    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    if not os.path.isdir(voices_dir):
        return []
    return sorted(f[:-3] for f in os.listdir(voices_dir) if f.endswith(".pt"))


# =============================================================================
# Image Generation
# =============================================================================

def generate_image(prompt, width=1024, height=1024, seed=None, steps=8):
    """Run the Z-Image Turbo pipeline and return the path to the saved image."""
    import random
    target = server_address_audio  # runs on ComfyUI_Main

    with open(os.path.join(SCRIPT_DIR, WORKFLOW_IMAGE_FILE), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    # Inject parameters
    workflow["57:27"]["inputs"]["text"] = prompt
    workflow["57:13"]["inputs"]["width"] = width
    workflow["57:13"]["inputs"]["height"] = height
    workflow["57:3"]["inputs"]["steps"] = steps
    workflow["57:3"]["inputs"]["seed"] = seed if seed is not None else random.randint(0, 2**53)

    print(f"[Image] Generating image for: '{prompt[:50]}...' (ComfyUI @ {target})")

    history = run_comfyui_workflow(workflow, target=target)
    file_info = retrieve_output_file(history, WORKFLOW_IMAGE_OUTPUT_NODE)
    return download_output(file_info, target=target)


# =============================================================================
# Video Generation
# =============================================================================

# --- Default negative prompt from the Wan2 workflow ---
_WAN2_DEFAULT_NEGATIVE = (
    "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，"
    "整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，"
    "画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，"
    "静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走"
)


def generate_video_wan2(image_path, prompt, negative_prompt=None,
                        width=640, height=640, length=301,
                        enable_4step_lora=True, seed=None, steps=20, cfg=3.5):
    """Run the Wan 2.2 Image-to-Video pipeline and return the saved video path."""
    import random
    target = server_address_audio  # runs on ComfyUI_Main

    with open(os.path.join(SCRIPT_DIR, WORKFLOW_VIDEO_WAN2_FILE), "r", encoding="utf-8") as f:
        workflow = json.load(f)

    # Upload image and inject into LoadImage node
    print(f"[Video/Wan2] Uploading {os.path.basename(image_path)}... (ComfyUI @ {target})")
    uploaded_name = upload_image(image_path, target=target)
    workflow["97"]["inputs"]["image"] = uploaded_name

    # Positive prompt
    workflow["129:93"]["inputs"]["text"] = prompt

    # Negative prompt
    workflow["129:89"]["inputs"]["text"] = negative_prompt or _WAN2_DEFAULT_NEGATIVE

    # WanImageToVideo settings
    workflow["129:98"]["inputs"]["width"] = width
    workflow["129:98"]["inputs"]["height"] = height
    workflow["129:98"]["inputs"]["length"] = length

    # 4-step LoRA toggle
    workflow["129:131"]["inputs"]["value"] = enable_4step_lora

    # Seed on the high-noise KSampler
    workflow["129:86"]["inputs"]["noise_seed"] = seed if seed is not None else random.randint(0, 2**53)

    # When LoRA is off, the switch nodes read from the "on_false" primitives,
    # so we inject steps and CFG there. When LoRA is on the switches use the
    # on_true primitives which are pre-set to 4 steps / 1.0 CFG.
    workflow["129:128"]["inputs"]["value"] = steps   # Steps (non-LoRA)
    workflow["129:126"]["inputs"]["value"] = cfg     # CFG   (non-LoRA)

    print(f"[Video/Wan2] Generating video for: '{prompt[:60]}...' "
          f"({width}x{height}, {length} frames, lora4step={enable_4step_lora})")

    history = run_comfyui_workflow(workflow, target=target)
    file_info = retrieve_output_file(history, WORKFLOW_VIDEO_WAN2_OUTPUT_NODE)
    return download_output(file_info, target=target)


def generate_video_veo(image_path, prompt):
    """Generate video using Google Veo 3.1 API and return the saved video path."""
    from google import genai

    if not GEMINI_API_KEY:
        raise ValueError("GEMINI_API_KEY not set in .env file")

    client = genai.Client(api_key=GEMINI_API_KEY)

    with open(image_path, "rb") as f:
        image_bytes = f.read()

    print(f"[Video/Veo] Submitting to Google Veo 3.1: '{prompt[:60]}...'")

    operation = client.models.generate_videos(
        model="veo-3.1-fast-generate-001",
        prompt=prompt,
        image=genai.types.Image(image_bytes=image_bytes, mime_type="image/png"),
    )

    print("[Video/Veo] Waiting for rendering (this may take several minutes)...")
    while not operation.done:
        time.sleep(10)
        operation = client.operations.get(operation)

    result = operation.result
    video = result.generated_videos[0]
    video_data = client.files.download(file=video.video)

    os.makedirs(RESULTS_DIR, exist_ok=True)
    output_path = os.path.join(RESULTS_DIR, f"veo_{uuid.uuid4().hex[:8]}.mp4")
    with open(output_path, "wb") as f:
        f.write(video_data)

    print(f"[Video/Veo] Success! Saved to: {output_path}")
    return output_path


def generate_video_runway(image_path, prompt, ratio="16:9", duration=10):
    """Generate video using Runway Gen-4 Turbo API and return the saved video path."""
    from runwayml import RunwayML

    if not RUNWAYML_API_SECRET:
        raise ValueError("RUNWAYML_API_SECRET not set in .env file")

    client = RunwayML(api_key=RUNWAYML_API_SECRET)

    with open(image_path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("utf-8")

    ext = os.path.splitext(image_path)[1].lower()
    mime = "image/png" if ext == ".png" else "image/jpeg"
    data_uri = f"data:{mime};base64,{b64}"

    print(f"[Video/Runway] Submitting to Runway Gen-4 Turbo: '{prompt[:60]}...' "
          f"(ratio={ratio}, duration={duration}s)")

    task = client.image_to_video.create(
        model="gen4_turbo",
        prompt_image=data_uri,
        prompt_text=prompt,
        ratio=ratio,
        duration=duration,
    )

    print("[Video/Runway] Waiting for rendering...")
    while task.status not in ("SUCCEEDED", "FAILED"):
        time.sleep(10)
        task = client.tasks.retrieve(task.id)

    if task.status == "FAILED":
        raise Exception(f"Runway generation failed: {task.failure}")

    # Download video from the output URL
    os.makedirs(RESULTS_DIR, exist_ok=True)
    output_path = os.path.join(RESULTS_DIR, f"runway_{uuid.uuid4().hex[:8]}.mp4")
    urllib.request.urlretrieve(task.output[0], output_path)

    print(f"[Video/Runway] Success! Saved to: {output_path}")
    return output_path


def _route_video_generation(image_path, provider, prompt, **kwargs):
    """Route video generation to the correct provider."""
    if provider == "wan2":
        return generate_video_wan2(
            image_path, prompt,
            negative_prompt=kwargs.get("negative_prompt"),
            width=int(kwargs.get("width", 640)),
            height=int(kwargs.get("height", 640)),
            length=int(kwargs.get("length", 301)),
            enable_4step_lora=kwargs.get("enable_4step_lora", "true").lower() == "true"
                if isinstance(kwargs.get("enable_4step_lora"), str)
                else bool(kwargs.get("enable_4step_lora", True)),
            seed=int(kwargs["seed"]) if kwargs.get("seed") else None,
            steps=int(kwargs.get("steps", 20)),
            cfg=float(kwargs.get("cfg", 3.5)),
        )
    elif provider == "veo":
        return generate_video_veo(image_path, prompt)
    elif provider == "runway":
        return generate_video_runway(
            image_path, prompt,
            ratio=kwargs.get("ratio", "16:9"),
            duration=int(kwargs.get("duration", 10)),
        )
    else:
        raise ValueError(f"Unknown video provider: {provider}")


# =============================================================================
# Flask API
# =============================================================================

app = Flask(__name__)


# Shared-secret auth. Set from --auth-token at startup. When non-empty, every
# request must carry a matching X-AI-Studio-Token header. /healthz is exempt
# so Unity can probe readiness before it has the token.
_AUTH_TOKEN = ""
_AUTH_HEADER = "X-AI-Studio-Token"
_AUTH_EXEMPT_PATHS = {"/healthz"}


@app.before_request
def _enforce_auth_token():
    if not _AUTH_TOKEN:
        return None
    if request.path in _AUTH_EXEMPT_PATHS:
        return None
    supplied = request.headers.get(_AUTH_HEADER, "")
    if supplied != _AUTH_TOKEN:
        return jsonify({"error": "unauthorized", "message": f"Missing or invalid {_AUTH_HEADER} header"}), 401
    return None


@app.route("/healthz", methods=["GET"])
def api_healthz():
    return jsonify({
        "status": "ok",
        "auth_required": bool(_AUTH_TOKEN),
        "comfy_3d": server_address_3d,
        "comfy_audio": server_address_audio,
    })


# =============================================================================
# Async Job System
# =============================================================================
# Long-running tasks (3D generation, etc.) can exceed Cloudflare tunnel timeouts.
# This job system lets clients submit work and poll for results.

_jobs = {}  # job_id -> {status, result_path, error, type}
_jobs_lock = threading.Lock()


def _run_job(job_id, func, *args, **kwargs):
    """Run a generation function in a background thread and store the result."""
    try:
        result_path = func(*args, **kwargs)
        with _jobs_lock:
            _jobs[job_id]["status"] = "complete"
            _jobs[job_id]["result_path"] = result_path
    except Exception as e:
        traceback.print_exc()
        with _jobs_lock:
            _jobs[job_id]["status"] = "error"
            _jobs[job_id]["error"] = str(e)


@app.route("/jobs/submit/3d", methods=["POST"])
def api_submit_3d():
    """Submit a 3D generation job. Returns a job ID immediately."""
    import tempfile
    temp_dir = tempfile.mkdtemp()

    f = request.files.get("image") or request.files.get("front")
    if f is None:
        files = request.files.getlist("images")
        f = files[0] if files else None
    if f is None:
        return jsonify({"error": "No image provided. Use field name 'image'."}), 400

    path = os.path.join(temp_dir, f.filename)
    f.save(path)

    job_id = str(uuid.uuid4())
    with _jobs_lock:
        _jobs[job_id] = {"status": "running", "result_path": None, "error": None, "type": "3d", "temp_dir": temp_dir}

    t = threading.Thread(target=_run_job, args=(job_id, generate_3d, path), daemon=True)
    t.start()

    return jsonify({"job_id": job_id, "status": "running"})


@app.route("/jobs/submit/image", methods=["POST"])
def api_submit_image():
    """Submit an image generation job. Returns a job ID immediately."""
    data = request.json
    if not data or "prompt" not in data:
        return jsonify({"error": "Missing 'prompt' in JSON payload"}), 400

    job_id = str(uuid.uuid4())
    with _jobs_lock:
        _jobs[job_id] = {"status": "running", "result_path": None, "error": None, "type": "image"}

    t = threading.Thread(
        target=_run_job,
        args=(job_id, generate_image, data["prompt"]),
        kwargs={
            "width": data.get("width", 1024),
            "height": data.get("height", 1024),
            "seed": data.get("seed"),
            "steps": data.get("steps", 8),
        },
        daemon=True,
    )
    t.start()

    return jsonify({"job_id": job_id, "status": "running"})


@app.route("/jobs/submit/audio", methods=["POST"])
def api_submit_audio():
    """Submit an audio generation job. Returns a job ID immediately."""
    data = request.json
    if not data or "text" not in data:
        return jsonify({"error": "Missing 'text' in JSON payload"}), 400

    job_id = str(uuid.uuid4())
    with _jobs_lock:
        _jobs[job_id] = {"status": "running", "result_path": None, "error": None, "type": "audio"}

    t = threading.Thread(
        target=_run_job,
        args=(job_id, generate_audio, data["text"]),
        kwargs={"character": data.get("character", "Female"), "style": data.get("style", "Warm")},
        daemon=True,
    )
    t.start()

    return jsonify({"job_id": job_id, "status": "running"})


@app.route("/jobs/submit/cloned-voice-audio", methods=["POST"])
def api_submit_cloned_voice_audio():
    """Submit a cloned-voice audio generation job (JSON, no file upload).

    Expects JSON: {text, voice_name, character?, style?}
    Uses the reference audio stored during voice cloning.
    """
    data = request.json
    if not data or "text" not in data:
        return jsonify({"error": "Missing 'text' in JSON payload"}), 400
    if "voice_name" not in data:
        return jsonify({"error": "Missing 'voice_name' in JSON payload"}), 400

    voice_name = data["voice_name"]
    ref_audio = _find_reference_audio(voice_name)
    if not ref_audio:
        return jsonify({
            "error": f"No stored reference audio for voice '{voice_name}'. "
                     f"Re-clone the voice to store the reference audio."
        }), 400

    job_id = str(uuid.uuid4())
    with _jobs_lock:
        _jobs[job_id] = {"status": "running", "result_path": None, "error": None, "type": "cloned_audio"}

    t = threading.Thread(
        target=_run_job,
        args=(job_id, generate_voice_clone_speech, data["text"], voice_name, ref_audio),
        kwargs={"character": data.get("character", "Female"), "style": data.get("style", "Warm")},
        daemon=True,
    )
    t.start()

    return jsonify({"job_id": job_id, "status": "running"})


@app.route("/jobs/<job_id>/status", methods=["GET"])
def api_job_status(job_id):
    """Check the status of a submitted job."""
    with _jobs_lock:
        job = _jobs.get(job_id)
    if not job:
        return jsonify({"error": "Job not found"}), 404
    return jsonify({"job_id": job_id, "status": job["status"], "error": job.get("error")})


@app.route("/jobs/<job_id>/cancel", methods=["POST"])
def api_job_cancel(job_id):
    """Cancel a running job. The background thread cannot be killed, but the
    job is marked as cancelled so poll clients stop waiting."""
    with _jobs_lock:
        job = _jobs.get(job_id)
    if not job:
        return jsonify({"error": "Job not found"}), 404
    if job["status"] != "running":
        return jsonify({"job_id": job_id, "status": job["status"], "message": "Job is not running"})
    with _jobs_lock:
        _jobs[job_id]["status"] = "cancelled"
        _jobs[job_id]["error"] = "Cancelled by user"
    return jsonify({"job_id": job_id, "status": "cancelled"})


@app.route("/jobs/<job_id>/result", methods=["GET"])
def api_job_result(job_id):
    """Download the result of a completed job."""
    with _jobs_lock:
        job = _jobs.get(job_id)
    if not job:
        return jsonify({"error": "Job not found"}), 404
    if job["status"] == "running":
        return jsonify({"error": "Job still running", "status": "running"}), 202
    if job["status"] == "error":
        return jsonify({"error": job["error"], "status": "error"}), 500
    if not job.get("result_path") or not os.path.isfile(job["result_path"]):
        return jsonify({"error": "Result file not found"}), 500

    response = send_file(job["result_path"], as_attachment=True)

    # Clean up temp dir if present
    temp_dir = job.get("temp_dir")
    if temp_dir:
        shutil.rmtree(temp_dir, ignore_errors=True)

    return response


@app.route("/generate/3d", methods=["POST"])
def api_generate_3d():
    """
    POST multipart/form-data with a single image file.
    Field name: 'image'.
    Returns the generated GLB file.
    """
    import tempfile
    temp_dir = tempfile.mkdtemp()

    try:
        # Accept 'image' field, or fall back to 'front' / first file in 'images' for compat
        f = request.files.get("image") or request.files.get("front")
        if f is None:
            files = request.files.getlist("images")
            f = files[0] if files else None
        if f is None:
            return jsonify({"error": "No image provided. Use field name 'image'."}), 400

        path = os.path.join(temp_dir, f.filename)
        f.save(path)

        output_path = generate_3d(path)
        return send_file(output_path, as_attachment=True)

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


@app.route("/generate/audio", methods=["POST"])
def api_generate_audio():
    """
    POST JSON: {"text": "...", "character": "Female", "style": "Warm"}
    Returns the generated audio file.
    """
    data = request.json
    if not data or "text" not in data:
        return jsonify({"error": "Missing 'text' in JSON payload"}), 400

    try:
        character = data.get("character", "Female")
        style = data.get("style", "Warm")
        output_path = generate_audio(data["text"], character=character, style=style)
        return send_file(output_path, as_attachment=True)

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500


@app.route("/voices", methods=["GET"])
def api_list_voices():
    """Return a list of available cloned voice names."""
    return jsonify({"voices": list_cloned_voices()})


@app.route("/voices/<name>/download", methods=["GET"])
def api_download_voice(name):
    """Stream the .pt file for a cloned voice so clients can cache it locally
    and re-upload to a different instance after termination."""
    safe = os.path.basename(name)
    if safe != name or not safe:
        return jsonify({"error": "invalid voice name"}), 400
    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    pt_path = os.path.join(voices_dir, safe + ".pt")
    if not os.path.isfile(pt_path):
        return jsonify({"error": f"voice '{safe}' not found"}), 404
    return send_file(pt_path, as_attachment=True, download_name=safe + ".pt",
                     mimetype="application/octet-stream")


@app.route("/voices/upload", methods=["POST"])
def api_upload_voice():
    """Accept a .pt file via multipart form-data ('voice' field) plus a
    'voice_name' form field. Writes to ComfyUI_Main/output/qwen3-tts_voices/."""
    f = request.files.get("voice")
    if f is None:
        return jsonify({"error": "No file provided. Use field name 'voice'."}), 400
    raw_name = request.form.get("voice_name", "")
    safe = os.path.basename(raw_name or "")
    if not safe or safe != raw_name:
        return jsonify({"error": "invalid voice_name"}), 400
    voices_dir = os.path.join(COMFYUI_MAIN_DIR, "output", "qwen3-tts_voices")
    os.makedirs(voices_dir, exist_ok=True)
    dest = os.path.join(voices_dir, safe + ".pt")
    f.save(dest)
    return jsonify({"voice_name": safe, "bytes": os.path.getsize(dest)})


@app.route("/generate/voice-clone", methods=["POST"])
def api_generate_voice_clone():
    """
    POST multipart/form-data:
      - 'audio': reference audio file
      - 'voice_name': name for the cloned voice (default: "voice_1")
      - 'language': STT language code (default: "zh")
    Returns JSON with the voice name on success.
    """
    import tempfile
    temp_dir = tempfile.mkdtemp()

    try:
        f = request.files.get("audio")
        if f is None:
            return jsonify({"error": "No audio file provided. Use field name 'audio'."}), 400

        voice_name = request.form.get("voice_name", "voice_1")
        language = request.form.get("language", "zh")

        path = os.path.join(temp_dir, f.filename)
        f.save(path)

        pt_path = generate_voice_clone(path, voice_name=voice_name, language=language)
        return jsonify({"voice_name": voice_name, "path": pt_path})

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


@app.route("/generate/voice-clone-speech", methods=["POST"])
def api_generate_voice_clone_speech():
    """
    POST multipart/form-data:
      - 'audio': reference audio file
      - 'text': text to speak
      - 'voice_name': name of the cloned voice to use
      - 'character': voice character (default: "Female")
      - 'style': voice style (default: "Warm")
    Returns the generated audio file.
    """
    import tempfile
    temp_dir = tempfile.mkdtemp()

    try:
        f = request.files.get("audio")
        if f is None:
            return jsonify({"error": "No audio file provided. Use field name 'audio'."}), 400

        text = request.form.get("text")
        if not text:
            return jsonify({"error": "Missing 'text' field."}), 400

        voice_name = request.form.get("voice_name")
        if not voice_name:
            return jsonify({"error": "Missing 'voice_name' field."}), 400

        character = request.form.get("character", "Female")
        style = request.form.get("style", "Warm")

        path = os.path.join(temp_dir, f.filename)
        f.save(path)

        output_path = generate_voice_clone_speech(
            text, voice_name, path, character=character, style=style
        )
        return send_file(output_path, as_attachment=True)

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


@app.route("/generate/image", methods=["POST"])
def api_generate_image():
    """
    POST JSON: {"prompt": "...", "width": 1024, "height": 1024, "seed": null, "steps": 8}
    Returns the generated image file.
    """
    data = request.json
    if not data or "prompt" not in data:
        return jsonify({"error": "Missing 'prompt' in JSON payload"}), 400

    try:
        raw_seed = data.get("seed")
        seed = None if raw_seed is None or raw_seed < 0 else raw_seed
        output_path = generate_image(
            prompt=data["prompt"],
            width=data.get("width", 1024),
            height=data.get("height", 1024),
            seed=seed,
            steps=data.get("steps", 8),
        )
        return send_file(output_path, as_attachment=True)

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500


def _parse_video_form(req):
    """Extract video generation parameters from a multipart form request."""
    import tempfile

    f = req.files.get("image")
    if f is None:
        return None, None, "No image provided. Use field name 'image'."

    provider = req.form.get("provider")
    if not provider:
        return None, None, "Missing 'provider' field (wan2, veo, or runway)."

    prompt = req.form.get("prompt")
    if not prompt:
        return None, None, "Missing 'prompt' field."

    temp_dir = tempfile.mkdtemp()
    path = os.path.join(temp_dir, f.filename)
    f.save(path)

    kwargs = {}
    for key in ("negative_prompt", "width", "height", "length",
                "enable_4step_lora", "seed", "steps", "cfg",
                "ratio", "duration"):
        val = req.form.get(key)
        if val is not None:
            kwargs[key] = val

    return (path, provider, prompt, kwargs), temp_dir, None


@app.route("/generate/video", methods=["POST"])
def api_generate_video():
    """
    POST multipart/form-data with an image file and form fields:
      - image: reference image (required)
      - provider: "wan2", "veo", or "runway" (required)
      - prompt: text prompt (required)
      - Plus provider-specific fields (see _route_video_generation)
    Returns the generated video file.
    """
    parsed, temp_dir, error = _parse_video_form(request)
    if error:
        return jsonify({"error": error}), 400

    path, provider, prompt, kwargs = parsed
    try:
        output_path = _route_video_generation(path, provider, prompt, **kwargs)
        return send_file(output_path, as_attachment=True)
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    finally:
        if temp_dir:
            shutil.rmtree(temp_dir, ignore_errors=True)


@app.route("/jobs/submit/video", methods=["POST"])
def api_submit_video():
    """Submit a video generation job. Returns a job ID immediately."""
    parsed, temp_dir, error = _parse_video_form(request)
    if error:
        return jsonify({"error": error}), 400

    path, provider, prompt, kwargs = parsed

    job_id = str(uuid.uuid4())
    with _jobs_lock:
        _jobs[job_id] = {
            "status": "running", "result_path": None, "error": None,
            "type": "video", "temp_dir": temp_dir,
        }

    t = threading.Thread(
        target=_run_job,
        args=(job_id, _route_video_generation, path, provider, prompt),
        kwargs=kwargs,
        daemon=True,
    )
    t.start()

    return jsonify({"job_id": job_id, "status": "running"})


# Legacy endpoints — keep backward compatibility with old Unity builds
@app.route("/generate", methods=["POST"])
def api_generate_legacy():
    """Route to the right handler based on content type."""
    content_type = request.content_type or ""
    if "application/json" in content_type:
        return api_generate_audio()
    else:
        return api_generate_3d()


# =============================================================================
# Entry Point
# =============================================================================

def _start_cloudflare_tunnel(port):
    """Spawn a cloudflared quick tunnel and print the public URL."""
    try:
        proc = subprocess.Popen(
            ["cloudflared", "tunnel", "--url", f"http://localhost:{port}"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError:
        print("[Tunnel] ERROR: 'cloudflared' not found on PATH.")
        print("[Tunnel] Install it: choco install cloudflared")
        print("[Tunnel]   or download from https://github.com/cloudflare/cloudflared/releases")
        return

    atexit.register(proc.terminate)

    def _read_tunnel_url():
        for line in proc.stderr:
            text = line.decode("utf-8", errors="replace").strip()
            if text:
                print(f"[Tunnel] {text}")
            match = re.search(r"(https://[a-zA-Z0-9\-]+\.trycloudflare\.com)", text)
            if match:
                print()
                print(f"  *** Tunnel URL: {match.group(1)} ***")
                print()

    t = threading.Thread(target=_read_tunnel_url, daemon=True)
    t.start()
    print("[Tunnel] Starting Cloudflare quick tunnel...")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="AI Studio ComfyUI Server (3D + Audio + Image)",
        epilog="Examples:\n"
               "  python run_comfy_server.py --port 5001\n"
               "  python run_comfy_server.py --comfy-3d 127.0.0.1:8001 --comfy-audio 127.0.0.1:8000\n"
               "\n"
               "  # CLI — 3D generation\n"
               "  python run_comfy_server.py --cli 3d --image model.png\n"
               "\n"
               "  # CLI — Audio generation\n"
               '  python run_comfy_server.py --cli audio --text "Hello world"\n'
               "\n"
               "  # CLI — Image generation (uses defaults)\n"
               "  python run_comfy_server.py --cli image\n",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--cli", type=str, nargs="?", const="3d", default=None,
        choices=["3d", "audio", "image"],
        help="Run in CLI mode: '3d', 'audio', or 'image'",
    )
    parser.add_argument("--image", type=str, help="Image path for 3D CLI mode")
    parser.add_argument("--text", type=str, default="This is a default test.", help="Text for audio CLI mode")
    parser.add_argument("--character", type=str, default="Female", help="Voice character (Female, Male)")
    parser.add_argument("--style", type=str, default="Warm", help="Voice style (Warm, Bright, etc.)")
    parser.add_argument("--port", type=int, default=5001, help="Port for the API server (default: 5001)")
    parser.add_argument("--comfy-3d", type=str, default=None,
                        help="ComfyUI Hunyuan (3D) address (default: 127.0.0.1:8001)")
    parser.add_argument("--comfy-audio", type=str, default=None,
                        help="ComfyUI Main (audio) address (default: 127.0.0.1:8000)")
    parser.add_argument("--comfy-server", type=str, default=None,
                        help="Override BOTH ComfyUI addresses (legacy, single-instance mode)")
    parser.add_argument("--comfy-3d-dir", type=str, default=None,
                        help="ComfyUI Hunyuan install directory (for reading textured GLBs)")
    parser.add_argument("--comfy-main-dir", type=str, default=None,
                        help="ComfyUI Main install directory (for reading cloned voices)")
    parser.add_argument("--tunnel", action="store_true",
                        help="Auto-launch a Cloudflare quick tunnel (requires cloudflared on PATH)")
    parser.add_argument("--auth-token", type=str, default=None,
                        help="Require X-AI-Studio-Token header on every request (except /healthz)")

    args = parser.parse_args()

    if args.auth_token:
        _AUTH_TOKEN = args.auth_token

    if args.comfy_server:
        server_address_3d = args.comfy_server
        server_address_audio = args.comfy_server
    if args.comfy_3d:
        server_address_3d = args.comfy_3d
    if args.comfy_audio:
        server_address_audio = args.comfy_audio
    if args.comfy_3d_dir:
        COMFYUI_HUNYUAN_DIR = args.comfy_3d_dir
    if args.comfy_main_dir:
        COMFYUI_MAIN_DIR = args.comfy_main_dir

    if args.cli == "3d":
        if not args.image:
            parser.error("--image is required for 3d CLI mode")
        resolved = os.path.abspath(args.image)
        if not os.path.isfile(resolved):
            parser.error(f"File not found: {resolved}")
        generate_3d(resolved)

    elif args.cli == "audio":
        generate_audio(args.text, character=args.character, style=args.style)

    elif args.cli == "image":
        generate_image("A beautiful landscape")

    else:
        print(f"AI Studio Server starting on http://127.0.0.1:{args.port}")
        print(f"ComfyUI backends:")
        print(f"  3D (Hunyuan):  {server_address_3d}")
        print(f"  Audio (Main):  {server_address_audio}")
        print(f"Endpoints:")
        print(f"  POST /generate/3d                 — 3D model generation (multipart form)")
        print(f"  POST /generate/audio              — Voice design generation (JSON)")
        print(f"  POST /generate/image              — Image generation (JSON)")
        print(f"  POST /generate/video              — Video generation (multipart form)")
        print(f"  POST /generate/voice-clone        — Clone voice from audio (multipart form)")
        print(f"  POST /generate/voice-clone-speech — Speech with cloned voice (multipart form)")
        print(f"  GET  /voices                      — List cloned voices")
        print(f"  POST /jobs/submit/video            — Async video generation job")
        print(f"  POST /generate                    — Legacy (auto-detects by content type)")
        print()

        if args.tunnel:
            _start_cloudflare_tunnel(args.port)

        app.run(host="0.0.0.0", port=args.port)
