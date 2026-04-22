#!/usr/bin/env bash
# AI Studio cloud bootstrap for Lambda Cloud Ubuntu 22.04 GPU instances.
#
# Runs via user_data. Must be idempotent — the systemd unit re-invokes it on
# reboot to make sure services come back up after a stop/start cycle.
#
# Required environment variables (injected by Unity via user_data_template.sh):
#   AI_STUDIO_TOKEN           shared-secret enforced by run_comfy_server.py --auth-token
#   FS_MOUNT                  absolute path where the Lambda persistent filesystem is mounted
#
# Optional:
#   AI_STUDIO_BUNDLE_URL      URL to ai-studio-light.tar.gz or .zip (default: GitHub release)
#   GEMINI_API_KEY            forwarded to /etc/ai-studio/.env for Veo video provider
#   RUNWAYML_API_SECRET       forwarded to /etc/ai-studio/.env for Runway video provider
#   SKIP_TRELLIS              if "1", skip the comfy_trellis env (Python 3.11 / CU128)

set -uo pipefail
exec > >(tee -a /var/log/ai-studio-bootstrap.log) 2>&1

STATE_DIR="/var/ai-studio"
mkdir -p "$STATE_DIR"
rm -f "$STATE_DIR/ready"
echo "=== AI Studio bootstrap started: $(date -u) ==="

: "${AI_STUDIO_TOKEN:?AI_STUDIO_TOKEN is required}"
: "${FS_MOUNT:=/lambda-fs/ai-studio-models}"
: "${AI_STUDIO_BUNDLE_URL:=https://github.com/TheWiselyBearded/ai-studio/releases/latest/download/ai-studio-light.zip}"
: "${SKIP_TRELLIS:=0}"

AISTUDIO_USER="${SUDO_USER:-ubuntu}"
AISTUDIO_HOME="$(getent passwd "$AISTUDIO_USER" | cut -d: -f6)"
TOOLS_DIR="$AISTUDIO_HOME/Tools"
MINIFORGE_DIR="$AISTUDIO_HOME/miniforge3"
BUNDLE_DIR="/opt/ai-studio"

run_as_user() {
  sudo -u "$AISTUDIO_USER" -H bash -lc "$1"
}

# -----------------------------------------------------------------------------
# Phase 1 — apt dependencies + cloudflared
# -----------------------------------------------------------------------------
phase_apt() {
  echo "--- [Phase 1] apt deps ---"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y
  apt-get install -y \
    git build-essential cmake ninja-build \
    ffmpeg libgl1 libglib2.0-0 \
    curl wget unzip jq \
    python3-dev

  # cloudflared from Cloudflare's Debian repo
  if ! command -v cloudflared >/dev/null 2>&1; then
    mkdir -p --mode=0755 /usr/share/keyrings
    curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg \
      | tee /usr/share/keyrings/cloudflare-main.gpg >/dev/null
    echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared $(lsb_release -cs) main" \
      > /etc/apt/sources.list.d/cloudflared.list
    apt-get update -y
    apt-get install -y cloudflared
  fi
}

# -----------------------------------------------------------------------------
# Phase 2 — Miniforge
# -----------------------------------------------------------------------------
phase_miniforge() {
  echo "--- [Phase 2] Miniforge ---"
  if [ ! -d "$MINIFORGE_DIR" ]; then
    local installer="/tmp/miniforge.sh"
    curl -fsSL -o "$installer" \
      "https://github.com/conda-forge/miniforge/releases/latest/download/Miniforge3-Linux-x86_64.sh"
    run_as_user "bash '$installer' -b -p '$MINIFORGE_DIR'"
    rm -f "$installer"
    run_as_user "'$MINIFORGE_DIR/bin/conda' init bash"
  fi
}

# -----------------------------------------------------------------------------
# Phase 3 — Conda envs
# -----------------------------------------------------------------------------
create_env() {
  local name="$1" pyver="$2"
  if run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda env list | grep -qE '^\s*$name\s'"; then
    echo "  [env] $name already exists, skipping create"
  else
    echo "  [env] creating $name (python=$pyver)"
    run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda create -y -n $name python=$pyver"
  fi
}

phase_envs() {
  echo "--- [Phase 3] conda envs ---"
  create_env comfy_main    3.10
  create_env comfy_hunyuan 3.10
  if [ "$SKIP_TRELLIS" != "1" ]; then
    create_env comfy_trellis 3.11
  fi
}

# -----------------------------------------------------------------------------
# Phase 4 — Clone ComfyUI + custom nodes per env, install deps, apply hotfixes
# -----------------------------------------------------------------------------
clone_or_update() {
  local url="$1" dest="$2"
  if [ -d "$dest/.git" ]; then
    run_as_user "cd '$dest' && git pull --ff-only || true"
  else
    run_as_user "git clone '$url' '$dest'"
  fi
}

install_comfy_main() {
  local env_name="comfy_main"
  local comfy_dir="$TOOLS_DIR/ComfyUI_Main"
  echo "--- [comfy_main] ---"
  mkdir -p "$TOOLS_DIR"
  chown -R "$AISTUDIO_USER:$AISTUDIO_USER" "$TOOLS_DIR"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 && \
    pip install -r '$comfy_dir/requirements.txt' && \
    pip install -U --pre comfyui-manager && \
    pip install 'transformers==4.57.3'"

  # QwenTTS custom node
  local qwen_dir="$comfy_dir/custom_nodes/ComfyUI-QwenTTS"
  clone_or_update https://github.com/1038lab/ComfyUI-QwenTTS.git "$qwen_dir"
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install -r '$qwen_dir/requirements.txt' && \
    pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 && \
    pip install 'transformers==4.57.3'"

  # Hotfix 1: QwenTTS @check_model_inputs decorator incompatibility
  local qwen_tokenizer="$qwen_dir/qwen_tts/core/tokenizer_12hz/modeling_qwen3_tts_tokenizer_v2.py"
  if [ -f "$qwen_tokenizer" ]; then
    sed -i \
      -e 's|^from transformers.utils.generic import check_model_inputs|# & (patched: AI Studio bootstrap)|' \
      -e 's|^@check_model_inputs()|# @check_model_inputs()  # (patched: AI Studio bootstrap)|' \
      "$qwen_tokenizer" || true
  fi

  # Hotfix 2: pad_token_id in QwenTTS model configs
  for cfg in $(run_as_user "find $comfy_dir -type f -name config.json -path '*qwen*' 2>/dev/null" || true); do
    if grep -q '"codec_pad_id"' "$cfg" && ! grep -q '"pad_token_id"' "$cfg"; then
      sed -i '/"codec_pad_id"/a\    "pad_token_id": 2150,' "$cfg" || true
    fi
  done
}

install_comfy_hunyuan() {
  local env_name="comfy_hunyuan"
  local comfy_dir="$TOOLS_DIR/ComfyUI_Hunyuan"
  echo "--- [comfy_hunyuan] ---"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"

  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 && \
    pip install -r '$comfy_dir/requirements.txt'"

  clone_or_update https://github.com/ltdrdata/ComfyUI-Manager.git "$comfy_dir/custom_nodes/ComfyUI-Manager"

  local hy_dir="$comfy_dir/custom_nodes/ComfyUI-Hunyuan3d-2-1"
  clone_or_update https://github.com/visualbruno/ComfyUI-Hunyuan3d-2-1 "$hy_dir"

  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install trimesh pyhocon addict hydra-core loguru diffusers hydra-zen \
                scikit-image plyfile pymeshlab pytorch-lightning \
                opencv-python-headless onnxruntime-gpu 'rembg[gpu]' ninja \
                xatlas yacs pytorch-msssim pygltflib timm torchtyping meshlib ffmpeg-python \
                accelerate pybind11 && \
    pip install git+https://github.com/NVlabs/nvdiffrast/ --no-build-isolation && \
    pip install torch-scatter --no-build-isolation"

  # Compile C++ renderers (Hunyuan3D 2.1 bundled)
  if [ -d "$hy_dir/hy3dpaint/custom_rasterizer" ]; then
    run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
      cd '$hy_dir/hy3dpaint/custom_rasterizer' && python setup.py install"
  fi
  if [ -d "$hy_dir/hy3dpaint/DifferentiableRenderer" ]; then
    run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
      cd '$hy_dir/hy3dpaint/DifferentiableRenderer' && python setup.py install"
  fi

  # Hotfix: transformers check_torch_load_is_safe blocks legit .bin loads
  local import_utils="$MINIFORGE_DIR/envs/$env_name/lib/python3.10/site-packages/transformers/utils/import_utils.py"
  if [ -f "$import_utils" ]; then
    python3 - "$import_utils" <<'PY' || true
import io, re, sys, pathlib
p = pathlib.Path(sys.argv[1])
src = p.read_text()
marker = "# AI Studio: disabled"
if marker in src:
    sys.exit(0)
new = re.sub(
    r'(def\s+check_torch_load_is_safe\s*\([^)]*\)\s*:\s*\n)(?:\s+[^\n]+\n)+',
    r'\1    pass  ' + marker + '\n',
    src, count=1,
)
p.write_text(new)
PY
  fi
}

install_comfy_trellis() {
  [ "$SKIP_TRELLIS" = "1" ] && { echo "--- [comfy_trellis] skipped ---"; return; }
  local env_name="comfy_trellis"
  local comfy_dir="$TOOLS_DIR/ComfyUI_Trellis"
  echo "--- [comfy_trellis] ---"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"

  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128 && \
    pip install -r '$comfy_dir/requirements.txt'"

  local trellis_dir="$comfy_dir/custom_nodes/ComfyUI-Trellis2"
  clone_or_update https://github.com/visualbruno/ComfyUI-Trellis2.git "$trellis_dir"
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install meshlib requests pymeshlab opencv-python scipy open3d plotly rembg && \
    pip install git+https://github.com/NVlabs/nvdiffrast/ --no-build-isolation"
}

phase_comfy() {
  echo "--- [Phase 4] ComfyUI envs ---"
  install_comfy_main
  install_comfy_hunyuan
  install_comfy_trellis
}

# -----------------------------------------------------------------------------
# Phase 5 — Symlink shared models directory
# -----------------------------------------------------------------------------
phase_models_link() {
  echo "--- [Phase 5] models symlink -> $FS_MOUNT ---"
  mkdir -p "$FS_MOUNT"
  for env_name in comfy_main comfy_hunyuan comfy_trellis; do
    [ "$env_name" = "comfy_trellis" ] && [ "$SKIP_TRELLIS" = "1" ] && continue
    local comfy_dir
    case "$env_name" in
      comfy_main)    comfy_dir="$TOOLS_DIR/ComfyUI_Main" ;;
      comfy_hunyuan) comfy_dir="$TOOLS_DIR/ComfyUI_Hunyuan" ;;
      comfy_trellis) comfy_dir="$TOOLS_DIR/ComfyUI_Trellis" ;;
    esac
    [ -d "$comfy_dir" ] || continue

    if [ -L "$comfy_dir/models" ] || [ ! -e "$comfy_dir/models" ]; then
      rm -rf "$comfy_dir/models"
      ln -s "$FS_MOUNT" "$comfy_dir/models"
      chown -h "$AISTUDIO_USER:$AISTUDIO_USER" "$comfy_dir/models"
    else
      # Upstream checkout has a real directory; move its contents to FS and replace with symlink.
      if [ -d "$comfy_dir/models" ] && [ -z "$(ls -A "$comfy_dir/models")" ]; then
        rmdir "$comfy_dir/models"
        ln -s "$FS_MOUNT" "$comfy_dir/models"
      fi
    fi
  done
}

# -----------------------------------------------------------------------------
# Phase 6 — Fetch ai-studio-light bundle + .env
# -----------------------------------------------------------------------------
phase_bundle() {
  echo "--- [Phase 6] ai-studio bundle ---"
  mkdir -p "$BUNDLE_DIR" /etc/ai-studio
  local tmp="/tmp/ai-studio-bundle"
  rm -rf "$tmp" && mkdir -p "$tmp"
  local archive="$tmp/bundle"
  curl -fsSL -o "$archive" "$AI_STUDIO_BUNDLE_URL"

  if file "$archive" | grep -qi zip; then
    unzip -q -o "$archive" -d "$tmp"
  else
    tar -xzf "$archive" -C "$tmp"
  fi

  local src
  src="$(find "$tmp" -maxdepth 2 -type f -name run_comfy_server.py -printf '%h\n' | head -n1)"
  if [ -z "$src" ]; then
    echo "[Phase 6] ERROR: run_comfy_server.py not found in bundle"
    exit 1
  fi
  cp -r "$src/." "$BUNDLE_DIR/"
  chown -R "$AISTUDIO_USER:$AISTUDIO_USER" "$BUNDLE_DIR"

  {
    echo "GEMINI_API_KEY=${GEMINI_API_KEY:-}"
    echo "RUNWAYML_API_SECRET=${RUNWAYML_API_SECRET:-}"
  } > /etc/ai-studio/.env
  chmod 640 /etc/ai-studio/.env
}

# -----------------------------------------------------------------------------
# Phase 7 — systemd units
# -----------------------------------------------------------------------------
write_unit() {
  local path="$1"
  shift
  cat > "$path" <<EOF
$@
EOF
  chmod 644 "$path"
}

phase_systemd() {
  echo "--- [Phase 7] systemd units ---"

  write_unit /etc/systemd/system/comfy-main.service "[Unit]
Description=ComfyUI Main (port 8000)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
WorkingDirectory=$TOOLS_DIR/ComfyUI_Main
ExecStart=$MINIFORGE_DIR/envs/comfy_main/bin/python main.py --port 8000 --listen 127.0.0.1 --enable-manager
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target"

  write_unit /etc/systemd/system/comfy-hunyuan.service "[Unit]
Description=ComfyUI Hunyuan (port 8001)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
WorkingDirectory=$TOOLS_DIR/ComfyUI_Hunyuan
ExecStart=$MINIFORGE_DIR/envs/comfy_hunyuan/bin/python main.py --port 8001 --listen 127.0.0.1
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target"

  if [ "$SKIP_TRELLIS" != "1" ]; then
    write_unit /etc/systemd/system/comfy-trellis.service "[Unit]
Description=ComfyUI Trellis (port 8002)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
WorkingDirectory=$TOOLS_DIR/ComfyUI_Trellis
ExecStart=$MINIFORGE_DIR/envs/comfy_trellis/bin/python main.py --port 8002 --listen 127.0.0.1
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target"
  fi

  # Flask server + Cloudflare tunnel (--tunnel spawns cloudflared itself)
  write_unit /etc/systemd/system/ai-studio.service "[Unit]
Description=AI Studio Flask API (port 5001) + Cloudflare tunnel
After=comfy-main.service comfy-hunyuan.service network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
EnvironmentFile=/etc/ai-studio/.env
Environment=AI_STUDIO_TOKEN=$AI_STUDIO_TOKEN
WorkingDirectory=$BUNDLE_DIR
ExecStart=$MINIFORGE_DIR/envs/comfy_main/bin/python $BUNDLE_DIR/run_comfy_server.py \\
    --port 5001 --tunnel \\
    --auth-token \${AI_STUDIO_TOKEN} \\
    --comfy-audio 127.0.0.1:8000 --comfy-3d 127.0.0.1:8001 \\
    --comfy-3d-dir $TOOLS_DIR/ComfyUI_Hunyuan \\
    --comfy-main-dir $TOOLS_DIR/ComfyUI_Main
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/ai-studio.log
StandardError=append:/var/log/ai-studio.log

[Install]
WantedBy=multi-user.target"

  # Tunnel URL watcher: tail the log, grep for the trycloudflare URL, persist it.
  cat > /usr/local/bin/ai-studio-tunnel-watch.sh <<'WATCH'
#!/usr/bin/env bash
set -u
STATE_DIR="/var/ai-studio"
mkdir -p "$STATE_DIR"
LOG="/var/log/ai-studio.log"
touch "$LOG"
: > "$STATE_DIR/tunnel.url"
tail -F "$LOG" 2>/dev/null | while IFS= read -r line; do
  if url=$(echo "$line" | grep -oE 'https://[a-zA-Z0-9-]+\.trycloudflare\.com' | head -n1); then
    if [ -n "$url" ]; then
      echo "$url" > "$STATE_DIR/tunnel.url"
      touch "$STATE_DIR/ready"
      exit 0
    fi
  fi
done
WATCH
  chmod +x /usr/local/bin/ai-studio-tunnel-watch.sh

  write_unit /etc/systemd/system/ai-studio-tunnel-watch.service "[Unit]
Description=Extract Cloudflare tunnel URL from AI Studio log
After=ai-studio.service
Requires=ai-studio.service

[Service]
Type=simple
ExecStart=/usr/local/bin/ai-studio-tunnel-watch.sh
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target"

  systemctl daemon-reload
  systemctl enable comfy-main.service comfy-hunyuan.service ai-studio.service ai-studio-tunnel-watch.service
  [ "$SKIP_TRELLIS" != "1" ] && systemctl enable comfy-trellis.service
  systemctl restart comfy-main.service comfy-hunyuan.service
  [ "$SKIP_TRELLIS" != "1" ] && systemctl restart comfy-trellis.service
  systemctl restart ai-studio.service ai-studio-tunnel-watch.service
}

# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------
phase_apt
phase_miniforge
phase_envs
phase_comfy
phase_models_link
phase_bundle
phase_systemd

echo "=== AI Studio bootstrap finished: $(date -u) ==="
echo "Waiting on tunnel URL..."
# The tunnel-watch service writes $STATE_DIR/ready when the URL appears.
# Unity polls for that file via SSH.
