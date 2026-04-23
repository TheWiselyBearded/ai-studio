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

# FS_MOUNT: where the persistent models filesystem is mounted. Lambda mounts at
# /lambda/nfs/<fs-name> but historically the repo assumed /lambda-fs/. If the
# user_data didn't set FS_MOUNT or set it to a path that doesn't exist, probe
# for any mounted filesystem under /lambda/nfs/ that matches our convention
# before falling back to the ephemeral /opt/ai-studio-models.
if [ -z "${FS_MOUNT:-}" ] || [ ! -d "${FS_MOUNT}" ]; then
  for candidate in /lambda/nfs/ai-studio-models-* /lambda-fs/ai-studio-models-*; do
    if [ -d "$candidate" ]; then FS_MOUNT="$candidate"; break; fi
  done
fi
: "${FS_MOUNT:=/opt/ai-studio-models}"
echo "[bootstrap] FS_MOUNT=$FS_MOUNT"
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
    ffmpeg libgl1 libglib2.0-0 libopengl0 libglu1-mesa libegl1 libxkbcommon0 \
    sox libsox-dev libsox-fmt-all \
    aria2 \
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
    # Python 3.12 matches the only cpython ABI for which ComfyUI-Trellis2
    # ships Linux wheels (wheels/Linux/Torch291/*-cp312-cp312-linux_x86_64.whl).
    # The Setup Guide's 3.11 is Windows-only; on Linux, 3.11 would force us
    # to build cumesh/flex_gemm/o_voxel/nvdiffrec_render from source.
    create_env comfy_trellis 3.12
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
  echo "--- [comfy_main] (mirrors Dual-Environment Setup Guide, Part 3) ---"
  mkdir -p "$TOOLS_DIR"
  chown -R "$AISTUDIO_USER:$AISTUDIO_USER" "$TOOLS_DIR"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"

  # Install the CU121 torch stack from PyTorch's own index (this is what the
  # Setup Guide's Windows conda command resolves to — `pytorch-cuda=12.1` on
  # conda-forge Linux currently degrades to a CPU build, so we go direct).
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch==2.5.1 torchvision==0.20.1 torchaudio==2.5.1 \
        --index-url https://download.pytorch.org/whl/cu121 && \
    pip install -r '$comfy_dir/requirements.txt'"

  # Setup Guide Phase 2: native comfyui-manager (V2 UI).
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install -U --pre comfyui-manager"

  # AI Studio Flask server runtime deps (not part of ComfyUI's requirements).
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install flask websocket-client python-dotenv requests"

  # Setup Guide Phase 4: QwenTTS custom node.
  local qwen_dir="$comfy_dir/custom_nodes/ComfyUI-QwenTTS"
  clone_or_update https://github.com/1038lab/ComfyUI-QwenTTS.git "$qwen_dir"
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install -r '$qwen_dir/requirements.txt' && \
    pip install sox onnxruntime librosa soundfile"

  # Setup Guide Phase 5 — Hotfix 1 "CUDA Wipeout": QwenTTS requirements.txt
  # asks for torch>=2.9.1 and silently upgrades torch to a PyPI wheel that
  # either downgrades to CPU or links libcudart.so.13. Remediation from the
  # Guide: uninstall torch and reinstall the CU121 GPU wheels. `--force-reinstall`
  # guarantees we override whatever version the previous step landed on.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip uninstall -y torch torchvision torchaudio && \
    pip install --force-reinstall torch==2.5.1 torchvision==0.20.1 torchaudio==2.5.1 \
        --index-url https://download.pytorch.org/whl/cu121"

  # Drop any stray nvidia-*-cu13 wheels that transitive deps pulled in (onnxruntime-gpu,
  # newer toolchain wheels, etc.). They get loaded before the cu12 ones in dlopen
  # order and trip CUDNN_STATUS_NOT_INITIALIZED against the cu121 torch.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip list 2>/dev/null | awk '/-cu13/ {print \$1}' | xargs -r pip uninstall -y" || true

  # Setup Guide Phase 5 — Hotfix 4: transformers 5.x drops 'default' RoPE key.
  # Also re-pin huggingface-hub into the 0.x series — transformers 4.57.3
  # imports fail with `huggingface-hub>=0.34.0,<1.0 is required` if a prior
  # install (diffusers, hf_hub_download extras, etc.) upgraded hub past 1.0.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install 'transformers==4.57.3' 'huggingface-hub>=0.34.0,<1.0'"

  # Setup Guide Phase 5 — Hotfix 2: @check_model_inputs decorator incompatibility.
  # -E + ^(\s*) prefix so indented decorators inside class bodies match too.
  local qwen_tokenizer="$qwen_dir/qwen_tts/core/tokenizer_12hz/modeling_qwen3_tts_tokenizer_v2.py"
  if [ -f "$qwen_tokenizer" ]; then
    sed -i -E \
      -e 's|^(\s*)from transformers.utils.generic import check_model_inputs|\1# & (patched: AI Studio bootstrap)|' \
      -e 's|^(\s*)@check_model_inputs\(\)|\1# @check_model_inputs()  # (patched: AI Studio bootstrap)|' \
      "$qwen_tokenizer" || true
  fi

  # Setup Guide Phase 5 — Hotfix 3: pad_token_id in QwenTTS model configs.
  # These config.json files live inside HuggingFace-cached model weights,
  # not in the node repo — they don't exist yet at install time and will be
  # patched by a runtime shim (Hunyuan does the same). Kept as best-effort
  # for pre-seeded models on the persistent FS.
  for cfg in $(run_as_user "find $comfy_dir -type f -name config.json -path '*qwen*' 2>/dev/null" || true); do
    if grep -q '"codec_pad_id"' "$cfg" && ! grep -q '"pad_token_id"' "$cfg"; then
      sed -i '/"codec_pad_id"/a\    "pad_token_id": 2150,' "$cfg" || true
    fi
  done
}

install_comfy_hunyuan() {
  local env_name="comfy_hunyuan"
  local comfy_dir="$TOOLS_DIR/ComfyUI_Hunyuan"
  echo "--- [comfy_hunyuan] (mirrors Dual-Environment Setup Guide, Part 2) ---"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"

  # Pin CU121 torch stack via PyTorch's index (Setup Guide intent; conda-forge
  # Linux doesn't ship pytorch-cuda=12.1 the way the Windows Guide assumes).
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch==2.5.1 torchvision==0.20.1 torchaudio==2.5.1 \
        --index-url https://download.pytorch.org/whl/cu121 && \
    pip install -r '$comfy_dir/requirements.txt'"

  clone_or_update https://github.com/ltdrdata/ComfyUI-Manager.git "$comfy_dir/custom_nodes/ComfyUI-Manager"

  local hy_dir="$comfy_dir/custom_nodes/ComfyUI-Hunyuan3d-2-1"
  clone_or_update https://github.com/visualbruno/ComfyUI-Hunyuan3d-2-1 "$hy_dir"

  # Setup Guide Phase 2 — Batch 1 "Core", Batch 2 "Vision Fixes", Batch 3 "3D Mesh",
  # Batch 4 "Hardware", Batch 5 "Build Isolation Bypass". One pip call is equivalent
  # but we split into two so `--no-build-isolation` only applies to the two packages
  # that actually need it.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install trimesh pyhocon addict hydra-core loguru diffusers hydra-zen \
                scikit-image plyfile pymeshlab pytorch-lightning \
                opencv-python-headless onnxruntime-gpu 'rembg[gpu]' ninja \
                xatlas yacs pytorch-msssim pygltflib timm torchtyping meshlib ffmpeg-python \
                accelerate pybind11 && \
    pip install git+https://github.com/NVlabs/nvdiffrast/ --no-build-isolation && \
    pip install torch-scatter --no-build-isolation"

  # Drop any stray nvidia-*-cu13 packages (onnxruntime-gpu + other deps can
  # pull them in). conda's pytorch-cuda=12.1 lock prevents this at the
  # meta-package level, but transitive wheels can still slip through.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip list 2>/dev/null | awk '/-cu13/ {print \$1}' | xargs -r pip uninstall -y" || true

  # Pin transformers to the Setup Guide's version and keep huggingface-hub in
  # the pre-1.0 series. diffusers + hydra-core + others often bump hub past
  # 1.0 as a transitive, which breaks transformers 4.57.3's import-time check
  # ("huggingface-hub>=0.34.0,<1.0 is required").
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install 'transformers==4.57.3' 'huggingface-hub>=0.34.0,<1.0'"

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
  # (CVE-2025-32434 guard — Setup Guide Part 2 Phase 4 Hotfix 2). Setup Guide:
  # "replaced the contents of def check_torch_load_is_safe(): with pass".
  #
  # Use a line-based walk rather than a multiline regex: a greedy regex
  # happily consumes adjacent indented def bodies and nukes other functions
  # (learned this by deleting is_torch_deterministic on a live instance).
  local import_utils="$MINIFORGE_DIR/envs/$env_name/lib/python3.10/site-packages/transformers/utils/import_utils.py"
  if [ -f "$import_utils" ]; then
    python3 - "$import_utils" <<'PY' || true
import re, sys, pathlib
p = pathlib.Path(sys.argv[1])
src = p.read_text()
marker = "# AI Studio: disabled"
if marker in src:
    sys.exit(0)
lines = src.splitlines(keepends=True)
out, i, patched = [], 0, False
while i < len(lines):
    if not patched and re.match(r'def\s+check_torch_load_is_safe\s*\(', lines[i]):
        out.append(lines[i])
        out.append('    return None  ' + marker + ' (CVE-2025-32434)\n')
        i += 1
        # Skip only the ORIGINAL function body: blank lines or indented lines.
        while i < len(lines):
            ln = lines[i]
            if ln.strip() == '' or ln.startswith((' ', '\t')):
                i += 1
                continue
            break
        patched = True
        continue
    out.append(lines[i])
    i += 1
if patched:
    p.write_text(''.join(out))
PY
  fi
}

install_comfy_trellis() {
  [ "$SKIP_TRELLIS" = "1" ] && { echo "--- [comfy_trellis] skipped ---"; return; }
  local env_name="comfy_trellis"
  local comfy_dir="$TOOLS_DIR/ComfyUI_Trellis"
  echo "--- [comfy_trellis] (Linux equivalent of Setup Guide Part 4) ---"
  clone_or_update https://github.com/comfyanonymous/ComfyUI.git "$comfy_dir"

  # Torch pin: the Linux Trellis2 wheels ship in two subdirs with different
  # GPU arch targets:
  #   wheels/Linux/Torch270/ — SASS for sm_80/86/89/90 (broad Ampere+Hopper)
  #   wheels/Linux/Torch291/ — SASS for sm_120 ONLY (Blackwell — RTX 50xx/B200)
  # PTX cannot JIT-downgrade across major arches, so Torch291 wheels ABORT
  # with cudaErrorNoKernelImageForDevice on anything older than sm_120. We
  # pick Torch270 for broad GPU compatibility (A10, A100, H100, RTX 4090).
  #
  # The Torch270 wheels aren't all pinned to torch 2.7.x despite the dir name:
  # o_voxel needs c10::SymBool::guard_or_false (torch 2.8+) while the shipped
  # nvdiffrast needs c10::cuda::SetDevice(int8) with NO bool arg (torch ≤ 2.7).
  # These are ABI-incompatible. torch 2.8.0 satisfies o_voxel, and we rebuild
  # nvdiffrast from source against 2.8 (it's open-source NVLabs code) to get
  # matching SetDevice(int8, bool) symbols.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install torch==2.8.0 torchvision==0.23.0 torchaudio==2.8.0 \
        --index-url https://download.pytorch.org/whl/cu128 && \
    pip install -r '$comfy_dir/requirements.txt'"

  # torch 2.8 + cu128 pins libcusparseLt.so.0 at 0.6.3 via its metadata; the
  # later 0.7.x wheel that some sub-deps pull doesn't expose the same SONAME
  # layout and torch then fails with libcusparseLt.so.0 not found.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install --no-deps 'nvidia-cusparselt-cu12==0.6.3'"

  local trellis_dir="$comfy_dir/custom_nodes/ComfyUI-Trellis2"
  clone_or_update https://github.com/visualbruno/ComfyUI-Trellis2.git "$trellis_dir"

  # Install the 5 shipped Torch270 Linux wheels (cumesh, flex_gemm,
  # custom_rasterizer, o_voxel + nvdiffrast stub we'll replace) with --no-deps.
  # Without --no-deps pip hits a resolver conflict: o_voxel 0.0.1 pins
  # cumesh==0.0.1 (from a JeffreyXiang/CuMesh git source) while the wheels
  # dir ships cumesh 1.0. The shipped cumesh 1.0 is what the node author
  # actually uses; --no-deps keeps it and we handle real runtime deps below.
  # Torch270 has NO nvdiffrec_render wheel — only Torch291 ships it, and the
  # MeshOnly pipeline doesn't need it (that's a texture-projection extension).
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install --no-deps \
      '$trellis_dir/wheels/Linux/Torch270/cumesh-1.0-cp312-cp312-linux_x86_64.whl' \
      '$trellis_dir/wheels/Linux/Torch270/flex_gemm-0.0.1-cp312-cp312-linux_x86_64.whl' \
      '$trellis_dir/wheels/Linux/Torch270/custom_rasterizer-0.1-cp312-cp312-linux_x86_64.whl' \
      '$trellis_dir/wheels/Linux/Torch270/o_voxel-0.0.1-cp312-cp312-linux_x86_64.whl'"

  # Build nvdiffrast from source against the installed torch 2.8.0 so its
  # c10::cuda::SetDevice symbol matches. TORCH_CUDA_ARCH_LIST covers Ampere
  # (A10/A100 sm_86/80), Ada (RTX 4090 sm_89), Hopper (H100 sm_90). We
  # deliberately skip the shipped Torch270 nvdiffrast wheel because it's
  # linked against the torch-2.7 SetDevice(int8) single-arg signature.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    TORCH_CUDA_ARCH_LIST='8.0;8.6;8.9;9.0' pip install --no-build-isolation --no-deps \
        git+https://github.com/NVlabs/nvdiffrast.git"

  # Sparse convolution backend. flex_gemm's submanifold_conv3d wheel targets
  # sm_120 ONLY and aborts on sm_86/89/90 with cudaErrorNoKernelImageForDevice.
  # spconv-cu121 ships prebuilt cp312 wheels that work on all Ampere+ GPUs and
  # is ABI-compatible with torch 2.8 via the standard CUDA driver. We set
  # conv_backend='spconv' in scratch_Trellis3D.json to use this instead.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install --no-deps spconv-cu121 'cumm-cu121<0.8.0,>=0.7.11' && \
    pip install pyyaml pybind11 fire pccm"

  # Node's declared pip deps + the real runtime deps pip surfaced as missing
  # after --no-deps (plyfile, trimesh are imported by o_voxel; zstandard,
  # easydict by cumesh). transformers is imported by trellis2/modules/
  # image_feature_extractor.py for DINOv3ViTModel — required even though
  # the node's requirements.txt doesn't list it.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install -r '$trellis_dir/requirements.txt' && \
    pip install plyfile trimesh zstandard easydict && \
    pip install transformers accelerate safetensors"

  # §7 Issue 3 recurrence: transformers/accelerate/open3d pull a torchaudio
  # version pin that drifts past the torch 2.8.0 we started with, breaking
  # torchaudio's extension load with `undefined symbol: torch_library_impl`.
  # ComfyUI's comfy/ldm/lightricks/vae/audio_vae.py imports torchaudio
  # unconditionally at startup, so this kills the service even though Trellis
  # doesn't use audio. Re-pin the full trio with matching +cu128 versions.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip install --force-reinstall --no-deps torch==2.8.0 torchvision==0.23.0 torchaudio==2.8.0 \
        --index-url https://download.pytorch.org/whl/cu128 && \
    pip install --no-deps 'nvidia-cusparselt-cu12==0.6.3'"

  # Also uninstall xformers if a transitive dep pulled it in. xformers
  # published wheels are cu130/cp310 and mismatch our cu128/cp312 — its
  # extensions crash on load. Trellis2's sparse attention has a try/except
  # fallback to torch SDPA so xformers isn't needed.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip uninstall -y xformers 2>/dev/null || true"

  # Drop any stray nvidia-*-cu13 wheels (onnxruntime-gpu, modern transformers,
  # etc.). Same guard as the other two envs — §7 Issue 3.
  run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
    pip list 2>/dev/null | awk '/-cu13/ {print \$1}' | xargs -r pip uninstall -y" || true

  # DINOv3 weights. ComfyUI-Trellis2/nodes.py resolves via
  #   folder_paths.models_dir / facebook / dinov3-vitl16-pretrain-lvd1689m /
  #       model.safetensors
  # and raises "Facebook Dinov3 model not found" if it's missing — NOT an
  # HF auto-download. The model is gated on Meta's license, so this step
  # only runs when HF_TOKEN is present in the environment (piped through
  # /etc/ai-studio/.env via user_data_template.sh). If absent, the service
  # starts fine but 3D generation will fail at the first Trellis node.
  local dinov3_dir="$comfy_dir/models/facebook/dinov3-vitl16-pretrain-lvd1689m"
  if [ -n "${HF_TOKEN:-}" ] && [ ! -s "$dinov3_dir/model.safetensors" ]; then
    echo "  [trellis] fetching DINOv3 weights via HF_TOKEN"
    mkdir -p "$dinov3_dir"
    chown -R "$AISTUDIO_USER:$AISTUDIO_USER" "$comfy_dir/models/facebook"
    run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
      HF_TOKEN='$HF_TOKEN' python -c \"
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id='facebook/dinov3-vitl16-pretrain-lvd1689m',
    local_dir='$dinov3_dir',
    allow_patterns=['*.json', '*.safetensors', 'LICENSE*'],
    token='$HF_TOKEN',
)
\"" || echo "  [trellis] DINOv3 fetch FAILED — accept license at HF and retry"
  elif [ -s "$dinov3_dir/model.safetensors" ]; then
    echo "  [trellis] DINOv3 weights already present — skipping fetch"
  else
    echo "  [trellis] HF_TOKEN not set — skipping DINOv3 fetch (3D gen will fail until present)"
  fi
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

    # Upstream ComfyUI checks in a `models/` directory with empty subfolders
    # (checkpoints/, clip/, vae/, ...). A naive "skip if non-empty" test
    # skips the symlink because of those placeholder subdirs. Instead, if
    # the directory exists and every file under it is zero bytes AND we
    # haven't already symlinked it, nuke it and replace with the symlink.
    if [ -L "$comfy_dir/models" ]; then
      # Already a symlink — re-point to current FS_MOUNT just in case.
      rm -f "$comfy_dir/models"
      ln -s "$FS_MOUNT" "$comfy_dir/models"
      chown -h "$AISTUDIO_USER:$AISTUDIO_USER" "$comfy_dir/models"
    elif [ -d "$comfy_dir/models" ]; then
      local real_files
      real_files="$(find "$comfy_dir/models" -type f -size +0c 2>/dev/null | head -n1)"
      if [ -z "$real_files" ]; then
        # Empty placeholder tree from the clone — remove it outright.
        rm -rf "$comfy_dir/models"
        ln -s "$FS_MOUNT" "$comfy_dir/models"
        chown -h "$AISTUDIO_USER:$AISTUDIO_USER" "$comfy_dir/models"
      else
        echo "  [models] $comfy_dir/models already has real weights; leaving it alone."
      fi
    else
      ln -s "$FS_MOUNT" "$comfy_dir/models"
      chown -h "$AISTUDIO_USER:$AISTUDIO_USER" "$comfy_dir/models"
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
  # AI_STUDIO_BUNDLE_URL can be http(s):// or file://.../local.zip. curl handles both.
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
    # DINOv3 (used by Trellis2) and BiRefNet are gated on HuggingFace; their
    # from_pretrained() calls need HF_TOKEN to authenticate. Also accepted
    # as HUGGINGFACE_HUB_TOKEN by the legacy client — both exported here so
    # either lookup wins.
    echo "HF_TOKEN=${HF_TOKEN:-}"
    echo "HUGGINGFACE_HUB_TOKEN=${HF_TOKEN:-}"
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

  # All three ComfyUI services append to their own log files so the Flask
  # server can tail them for rich error context and expose them via /logs.
  touch /var/log/comfy-main.log /var/log/comfy-hunyuan.log /var/log/comfy-trellis.log
  chown "$AISTUDIO_USER:$AISTUDIO_USER" /var/log/comfy-*.log

  write_unit /etc/systemd/system/comfy-main.service "[Unit]
Description=ComfyUI Main (port 8000)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
Environment=PYTHONUNBUFFERED=1
WorkingDirectory=$TOOLS_DIR/ComfyUI_Main
ExecStart=$MINIFORGE_DIR/envs/comfy_main/bin/python main.py --port 8000 --listen 127.0.0.1 --enable-manager
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/comfy-main.log
StandardError=append:/var/log/comfy-main.log

[Install]
WantedBy=multi-user.target"

  write_unit /etc/systemd/system/comfy-hunyuan.service "[Unit]
Description=ComfyUI Hunyuan (port 8001)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
EnvironmentFile=/etc/ai-studio/.env
Environment=PYTHONUNBUFFERED=1
WorkingDirectory=$TOOLS_DIR/ComfyUI_Hunyuan
ExecStart=$MINIFORGE_DIR/envs/comfy_hunyuan/bin/python main.py --port 8001 --listen 127.0.0.1
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/comfy-hunyuan.log
StandardError=append:/var/log/comfy-hunyuan.log

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
EnvironmentFile=/etc/ai-studio/.env
Environment=PYTHONUNBUFFERED=1
WorkingDirectory=$TOOLS_DIR/ComfyUI_Trellis
ExecStart=$MINIFORGE_DIR/envs/comfy_trellis/bin/python main.py --port 8002 --listen 127.0.0.1
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/comfy-trellis.log
StandardError=append:/var/log/comfy-trellis.log

[Install]
WantedBy=multi-user.target"
  fi

  # Flask server + Cloudflare tunnel (--tunnel spawns cloudflared itself).
  # PYTHONUNBUFFERED=1 is essential: systemd logs go to a file (not a TTY), so
  # without it the [Tunnel] print() calls from the reader thread get buffered
  # and the tunnel-watch service never sees the URL line.
  write_unit /etc/systemd/system/ai-studio.service "[Unit]
Description=AI Studio Flask API (port 5001) + Cloudflare tunnel
After=comfy-main.service comfy-hunyuan.service network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$AISTUDIO_USER
EnvironmentFile=/etc/ai-studio/.env
Environment=AI_STUDIO_TOKEN=$AI_STUDIO_TOKEN
Environment=PYTHONUNBUFFERED=1
WorkingDirectory=$BUNDLE_DIR
ExecStart=$MINIFORGE_DIR/envs/comfy_main/bin/python $BUNDLE_DIR/run_comfy_server.py \\
    --port 5001 --tunnel \\
    --auth-token \${AI_STUDIO_TOKEN} \\
    --comfy-audio 127.0.0.1:8000 --comfy-3d 127.0.0.1:8001 --comfy-trellis 127.0.0.1:8002 \\
    --comfy-3d-dir $TOOLS_DIR/ComfyUI_Hunyuan \\
    --comfy-main-dir $TOOLS_DIR/ComfyUI_Main \\
    --comfy-trellis-dir $TOOLS_DIR/ComfyUI_Trellis
Restart=on-failure
RestartSec=5
StandardOutput=append:/var/log/ai-studio.log
StandardError=append:/var/log/ai-studio.log

[Install]
WantedBy=multi-user.target"

  # Tunnel URL watcher: tail the log, grep for the trycloudflare URL, persist it.
  # MUST NOT exit after the first URL — ai-studio.service gets restarted on
  # errors or by the user, and each restart gives cloudflared a fresh quick
  # tunnel URL. The watcher stays running and updates tunnel.url whenever the
  # URL changes; Unity polls this file over SSH to refresh its endpoint.
  cat > /usr/local/bin/ai-studio-tunnel-watch.sh <<'WATCH'
#!/usr/bin/env bash
set -u
STATE_DIR="/var/ai-studio"
mkdir -p "$STATE_DIR"
LOG="/var/log/ai-studio.log"
touch "$LOG"
CURRENT=""
if [ -f "$STATE_DIR/tunnel.url" ]; then CURRENT="$(cat "$STATE_DIR/tunnel.url")"; fi
tail -F "$LOG" 2>/dev/null | while IFS= read -r line; do
  url=$(echo "$line" | grep -oE 'https://[a-zA-Z0-9-]+\.trycloudflare\.com' | head -n1)
  if [ -n "$url" ] && [ "$url" != "$CURRENT" ]; then
    echo "$url" > "$STATE_DIR/tunnel.url"
    touch "$STATE_DIR/ready"
    CURRENT="$url"
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
