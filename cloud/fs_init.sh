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
apt-get install -y aria2 git-lfs curl
git lfs install --skip-repo

hf_download() {
  # hf_download <repo> <file> <dest_dir>
  local repo="$1" file="$2" dest="$3"
  mkdir -p "$dest"
  local url="https://huggingface.co/$repo/resolve/main/$file"
  echo "  -> $url"
  aria2c -x 8 -s 8 -c --dir="$dest" --out="$(basename "$file")" "$url" || \
    curl -fL --retry 3 -o "$dest/$(basename "$file")" "$url"
}

hf_clone() {
  # hf_clone <repo> <dest>
  local repo="$1" dest="$2"
  if [ -d "$dest/.git" ]; then
    (cd "$dest" && git lfs pull) || true
  else
    git clone "https://huggingface.co/$repo" "$dest"
  fi
}

# -----------------------------------------------------------------------------
# 3D: Hunyuan3D 2.1 PBR
# -----------------------------------------------------------------------------
hf_download tencent/Hunyuan3D-2.1 hunyuan3d-dit-v2-1/model.fp16.safetensors  "$FS_MOUNT/diffusion_models"
hf_download tencent/Hunyuan3D-2.1 hunyuan3d-paintpbr-v2-1/model.fp16.safetensors "$FS_MOUNT/diffusion_models"

# -----------------------------------------------------------------------------
# Image: Z-Image Turbo
# -----------------------------------------------------------------------------
hf_download tencent/ZImage-Turbo zimage-turbo.safetensors "$FS_MOUNT/checkpoints"

# -----------------------------------------------------------------------------
# Video: Wan 2.2
# -----------------------------------------------------------------------------
hf_download Comfy-Org/Wan_2.2 wan2.2-t2v-14b.safetensors     "$FS_MOUNT/diffusion_models"
hf_download Comfy-Org/Wan_2.2 wan2.2-lora-4step.safetensors  "$FS_MOUNT/loras"

# -----------------------------------------------------------------------------
# Audio / TTS: QwenTTS 1.7B
# -----------------------------------------------------------------------------
hf_clone Qwen/Qwen3-TTS-1.7B "$FS_MOUNT/TTS/Qwen3-TTS-1.7B"

# -----------------------------------------------------------------------------
# Trellis2: DINOv3
# -----------------------------------------------------------------------------
hf_clone facebook/dinov3-vitl16-pretrain-lvd1689m "$FS_MOUNT/facebook/dinov3-vitl16-pretrain-lvd1689m"

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
