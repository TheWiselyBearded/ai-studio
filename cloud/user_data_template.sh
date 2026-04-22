#!/usr/bin/env bash
# Lambda Cloud user_data template. Tiny wrapper that the Unity launch flow
# renders with the real values and passes to /instance-operations/launch.
#
# Template variables (filled in by Unity):
#   {{AI_STUDIO_TOKEN}}        32-byte random token, matched by --auth-token
#   {{FS_MOUNT}}               mount path of the attached persistent filesystem
#   {{AI_STUDIO_BUNDLE_URL}}   URL to ai-studio-light.zip (GitHub release asset)
#   {{BOOTSTRAP_URL}}          URL to the matching bootstrap.sh
#   {{GEMINI_API_KEY}}         optional
#   {{RUNWAYML_API_SECRET}}    optional
#   {{SKIP_TRELLIS}}           "1" to skip comfy_trellis, "0" otherwise

set -u
exec > /var/log/ai-studio-user-data.log 2>&1

export AI_STUDIO_TOKEN="{{AI_STUDIO_TOKEN}}"
export FS_MOUNT="{{FS_MOUNT}}"
export AI_STUDIO_BUNDLE_URL="{{AI_STUDIO_BUNDLE_URL}}"
export GEMINI_API_KEY="{{GEMINI_API_KEY}}"
export RUNWAYML_API_SECRET="{{RUNWAYML_API_SECRET}}"
export SKIP_TRELLIS="{{SKIP_TRELLIS}}"

curl -fsSL "{{BOOTSTRAP_URL}}" -o /tmp/bootstrap.sh
chmod +x /tmp/bootstrap.sh
bash /tmp/bootstrap.sh
