#!/usr/bin/env bash
# One-shot model filesystem seeder.
#
# Runs once per Lambda region to populate a persistent filesystem with every
# model weight AI Studio's workflow JSONs reference. After this script finishes
# it writes $FS_MOUNT/.init_done and calls Lambda's terminate-self so the job
# doesn't bill indefinitely.
#
# Required env:
#   FS_MOUNT                 persistent FS mount path (e.g. /lambda-fs/ai-studio-models)
#   LAMBDA_API_KEY           used to self-terminate when done
#   LAMBDA_INSTANCE_ID       passed via user_data from Unity
#
# NOTE: the exact model inventory below is derived from the workflow JSONs in
# ai-studio-light. Update this list whenever a new workflow/model is added.

set -uo pipefail
exec > >(tee -a /var/log/ai-studio-fs-init.log) 2>&1
echo "=== AI Studio FS init started: $(date -u) ==="

: "${FS_MOUNT:?FS_MOUNT is required}"
: "${LAMBDA_API_KEY:?LAMBDA_API_KEY is required}"
: "${LAMBDA_INSTANCE_ID:?LAMBDA_INSTANCE_ID is required}"

if [ -f "$FS_MOUNT/.init_done" ]; then
  echo "Filesystem already initialized."
  exit 0
fi

mkdir -p \
  "$FS_MOUNT/checkpoints" \
  "$FS_MOUNT/diffusion_models" \
  "$FS_MOUNT/clip" \
  "$FS_MOUNT/vae" \
  "$FS_MOUNT/loras" \
  "$FS_MOUNT/upscale_models" \
  "$FS_MOUNT/text_encoders" \
  "$FS_MOUNT/3d" \
  "$FS_MOUNT/TTS" \
  "$FS_MOUNT/facebook"

export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y aria2 git-lfs curl python3-pip
git lfs install --skip-repo
# huggingface_hub is used for gated/sharded repos where raw aria2c over
# /resolve/main/... can't enumerate siblings or auth cleanly.
pip3 install --quiet --break-system-packages 'huggingface_hub>=0.24,<2'

: "${HF_TOKEN:=}"
AUTH_HEADER_FLAG=()
if [ -n "$HF_TOKEN" ]; then
  AUTH_HEADER_FLAG=(--header "Authorization: Bearer $HF_TOKEN")
fi

hf_download() {
  # hf_download <repo> <file> <dest_dir>
  # Public repos work without HF_TOKEN; gated repos (DINOv3, some Llama weights)
  # require it and return 401 without the Authorization header.
  local repo="$1" file="$2" dest="$3"
  mkdir -p "$dest"
  local url="https://huggingface.co/$repo/resolve/main/$file"
  echo "  -> $url"
  aria2c -x 8 -s 8 -c --dir="$dest" --out="$(basename "$file")" "${AUTH_HEADER_FLAG[@]}" "$url" || \
    curl -fL --retry 3 ${HF_TOKEN:+-H "Authorization: Bearer $HF_TOKEN"} \
        -o "$dest/$(basename "$file")" "$url"
}

hf_snapshot() {
  # hf_snapshot <repo> <dest_dir> [allow_pattern ...]
  # Uses huggingface_hub.snapshot_download — the robust path for gated models.
  local repo="$1" dest="$2"; shift 2
  local patterns_py="None"
  if [ "$#" -gt 0 ]; then
    patterns_py="[$(printf "'%s'," "$@" | sed 's/,$//')]"
  fi
  python3 - <<PY
from huggingface_hub import snapshot_download
import os
os.makedirs("$dest", exist_ok=True)
snapshot_download(
    repo_id="$repo",
    local_dir="$dest",
    token=os.environ.get("HF_TOKEN") or None,
    allow_patterns=${patterns_py},
    max_workers=4,
)
PY
}

# -----------------------------------------------------------------------------
# 3D: Hunyuan3D 2.1 PBR
# The tencent/Hunyuan3D-2.1 repo stores weights as .ckpt (not .safetensors) and
# the ComfyUI-Hunyuan3d-2-1 node fetches them itself on first inference, so we
# don't need to pre-seed them here. Left as a placeholder to document the
# pipeline lives in comfy_hunyuan env.
# -----------------------------------------------------------------------------

# -----------------------------------------------------------------------------
# Image: Z-Image Turbo — repackaged single-file ComfyUI form.
# scratch_ZImageTurbo.json expects:
#   diffusion_models/z_image_turbo_bf16.safetensors
#   text_encoders/qwen_3_4b.safetensors      (also searched by CLIPLoader via clip/)
#   vae/ae.safetensors
# Source: Comfy-Org/z_image_turbo  (public, no auth needed)
# -----------------------------------------------------------------------------
hf_download Comfy-Org/z_image_turbo split_files/diffusion_models/z_image_turbo_bf16.safetensors "$FS_MOUNT/diffusion_models"
hf_download Comfy-Org/z_image_turbo split_files/text_encoders/qwen_3_4b.safetensors             "$FS_MOUNT/text_encoders"
hf_download Comfy-Org/z_image_turbo split_files/vae/ae.safetensors                              "$FS_MOUNT/vae"

# -----------------------------------------------------------------------------
# Video: Wan 2.2 I2V (image-to-video, 14B fp8). Two UNets (high/low noise), one
# text encoder (UMT5), one VAE (inherits Wan 2.1's), plus the 4-step lightx2v
# LoRAs that scratch_wan2_Video.json chains in.
# Source: Comfy-Org/Wan_2.2_ComfyUI_Repackaged  (public, no auth needed)
# -----------------------------------------------------------------------------
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors "$FS_MOUNT/diffusion_models"
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors  "$FS_MOUNT/diffusion_models"
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors              "$FS_MOUNT/text_encoders"
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/vae/wan_2.1_vae.safetensors                                       "$FS_MOUNT/vae"
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors   "$FS_MOUNT/loras"
hf_download Comfy-Org/Wan_2.2_ComfyUI_Repackaged split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors    "$FS_MOUNT/loras"

# Legacy CLIPLoader compatibility: some ComfyUI versions only search models/clip/
# for CLIP-style encoders. Symlink the text_encoders files so the workflows can
# find them under either path.
mkdir -p "$FS_MOUNT/clip"
ln -sf "../text_encoders/qwen_3_4b.safetensors"                "$FS_MOUNT/clip/qwen_3_4b.safetensors"
ln -sf "../text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors" "$FS_MOUNT/clip/umt5_xxl_fp8_e4m3fn_scaled.safetensors"

# -----------------------------------------------------------------------------
# Audio / TTS: QwenTTS 1.7B (public)
# -----------------------------------------------------------------------------
hf_snapshot Qwen/Qwen3-TTS-1.7B "$FS_MOUNT/TTS/Qwen3-TTS-1.7B"

# -----------------------------------------------------------------------------
# Trellis2: DINOv3 (GATED — requires HF_TOKEN with license accepted at
# https://huggingface.co/facebook/dinov3-vitl16-pretrain-lvd1689m). Skipped
# cleanly if HF_TOKEN is missing so we don't fail the whole init run.
# -----------------------------------------------------------------------------
if [ -n "$HF_TOKEN" ]; then
  hf_snapshot facebook/dinov3-vitl16-pretrain-lvd1689m \
    "$FS_MOUNT/facebook/dinov3-vitl16-pretrain-lvd1689m" \
    "*.json" "*.safetensors" "LICENSE*"
else
  echo "WARNING: HF_TOKEN not set — skipping DINOv3 fetch. Trellis2 will 401 at runtime."
fi

# Config for extra_model_paths.yaml (copied to each ComfyUI env at launch).
cat > "$FS_MOUNT/extra_model_paths.yaml" <<EOF
comfyui:
    base_path: $FS_MOUNT/
    checkpoints: checkpoints/
    diffusion_models: diffusion_models/
    clip: clip/
    vae: vae/
    loras: loras/
    text_encoders: text_encoders/
    upscale_models: upscale_models/
EOF

touch "$FS_MOUNT/.init_done"
echo "=== AI Studio FS init finished: $(date -u) ==="

# Self-terminate via Lambda API
curl -fsS -u "$LAMBDA_API_KEY:" \
  -H "Content-Type: application/json" \
  -d "{\"instance_ids\": [\"$LAMBDA_INSTANCE_ID\"]}" \
  -X POST https://cloud.lambda.ai/api/v1/instance-operations/terminate \
  || echo "WARNING: terminate call failed; shutting down locally instead"

shutdown -h now
