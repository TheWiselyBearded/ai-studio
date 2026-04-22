# AI Studio — System Architecture & Runbook

> **Audience:** the next agent or collaborator picking up this codebase.
> **Goal:** give you a complete enough picture to understand how AI Studio is
> wired together, how to ship changes without breaking the cloud flow, and
> where the sharp edges are.
>
> This doc supersedes scattered notes in the repo. Where deep detail lives
> elsewhere, it's linked.

---

## 1. Intent in one sentence

AI Studio is a Unity 6 editor-only tool that lets designers run ComfyUI-backed
generative pipelines (image, audio, voice clone, 3D mesh, video) either against
a local Flask server or against a Lambda Cloud GPU instance that the editor
itself spins up, provisions, and tears down.

---

## 2. Architecture at a glance

```
┌────────────────────────────────────────────────────────────────────┐
│ Unity Editor (Windows)                                             │
│                                                                    │
│  AI Studio/Image Generator    ┐                                    │
│  AI Studio/Voice Design       │                                    │
│  AI Studio/Voice Clone        │─ every window uses AIStudioClient ─┼─┐
│  AI Studio/Hunyuan 3D         │                                    │ │
│  AI Studio/Video Generator    ┘                                    │ │
│                                                                    │ │
│  AI Studio/Settings                         (EditorPrefs + JSON)   │ │
│  AI Studio/Cloud/Instance Manager  (Lambda REST + SSH readback)    │ │
│                                                                    │ │
│  Source of truth for remote URL:                                   │ │
│    Assets/StreamingAssets/AIStudio/endpoint.json  (gitignored)     │ │
└────────────────────────────────────────────────────────────────────┘ │
                                                                       │
                              ┌────────────────────────────────────────┘
                              │ HTTPS + X-AI-Studio-Token
                              ▼
┌──────────────────────────────────────────────────────────────┐
│ Cloudflare quick tunnel  (*.trycloudflare.com)               │
│   rotates on every ai-studio.service restart                 │
└─────────┬────────────────────────────────────────────────────┘
          │
          ▼
┌──────────────────────────────────────────────────────────────┐
│ Lambda Cloud gpu_1x_a10  (Ubuntu 22.04, CUDA 12.8 driver)    │
│                                                              │
│  /var/ai-studio/tunnel.url  ← tunnel-watch.sh                │
│  /var/log/{ai-studio,comfy-main,comfy-hunyuan}.log           │
│                                                              │
│  ┌── systemd units ────────────────────────────────────┐     │
│  │ ai-studio.service                                   │     │
│  │   run_comfy_server.py --port 5001 --tunnel          │     │
│  │                       --auth-token $TOKEN           │     │
│  │                                                     │     │
│  │ ai-studio-tunnel-watch.service                      │     │
│  │   tail -F /var/log/ai-studio.log → tunnel.url       │     │
│  │                                                     │     │
│  │ comfy-main.service         port 8000  (conda env)   │     │
│  │ comfy-hunyuan.service      port 8001  (conda env)   │     │
│  │ comfy-trellis.service      port 8002  (optional)    │     │
│  └─────────────────────────────────────────────────────┘     │
│                                                              │
│  ~/Tools/ComfyUI_{Main,Hunyuan,Trellis}/                     │
│    models/ → symlink to /opt/ai-studio-models/ (ephemeral)   │
│              or /lambda-fs/… (persistent FS, future)         │
└──────────────────────────────────────────────────────────────┘
```

Control plane (Lambda REST API) is used only to **launch / list / terminate**
instances. All actual inference traffic goes over the Cloudflare tunnel.

---

## 3. Repo layout

```
ai_studio/
├── ARCHITECTURE.md                 ← you are here
├── README.md                       ← high-level intent + legacy CLI docs
├── TUNNEL_SETUP_GUIDE.md           ← cloudflared mechanics
├── Dual-Environment ComfyUI Architecture Setup Guide.md
│                                    ← original Windows setup, source of truth
│                                       for which hotfixes the nodes need
├── .env.example                    ← template; real .env is gitignored
├── .gitignore / .gitattributes     ← LFS for binary media, ignore runtime
│                                     artifacts + StreamingAssets/AIStudio
│
├── run_comfy_server.py             ← Flask API, --tunnel, --auth-token
├── run_comfy_api.py                ← legacy audio-only API (still shipped)
├── convert_dinov3_pth_to_hf.py     ← utility for DINOv3 model conversion
├── Scratch_*.json / scratch_*.json ← ComfyUI workflow definitions
│
├── cloud/                          ← Lambda deployment
│   ├── bootstrap.sh                ← idempotent provisioner (the work horse)
│   ├── fs_init.sh                  ← one-shot model-FS seeder
│   ├── user_data_template.sh      ← Lambda injects this at launch time
│   └── BOOTSTRAP_DEBUG.md          ← field notes from real runs (every issue)
│
├── ai-studio-light/                ← staging dir for release zip (gitignored)
├── ai-studio-light.zip             ← release artifact (gitignored; attached
│                                     to v0.1.0 release instead)
│
└── Unity_AI_Studio/
    └── AI_Studio_Engine/           ← Unity 6 editor-only project
        ├── Packages/manifest.json  ← newtonsoft-json 3.2.1 added for Lambda
        │                             JSON parsing
        └── Assets/
            ├── Editor/             ← all our code lives here
            │   ├── Core/
            │   │   ├── AIStudioSettings.cs        ← EditorPrefs + manifest
            │   │   ├── AIStudioClient.cs          ← shared HttpClient +
            │   │   │                                 EnsureSuccessAsync
            │   │   └── AIStudioEndpointManifest.cs ← JSON file sync
            │   ├── AIStudioSettingsWindow.cs
            │   │
            │   ├── Hunyuan3DGeneratorWindow.cs    ← async-job pattern
            │   ├── ImageGeneratorWindow.cs
            │   ├── VideoGeneratorWindow.cs        ← async-job pattern
            │   ├── VoiceCloneWindow.cs
            │   ├── VoiceDesignGeneratorWindow.cs
            │   │
            │   ├── Lambda/
            │   │   ├── LambdaClient.cs            ← REST wrapper (Newtonsoft)
            │   │   ├── LambdaInstanceWindow.cs    ← launch + terminate UI
            │   │   ├── LambdaInstanceState.cs     ← persistent state
            │   │   ├── LambdaSshReadback.cs       ← SSH tunnel URL fetch
            │   │   ├── LambdaQuitGuard.cs         ← prevents billing leaks
            │   │   ├── LambdaCostAlarm.cs         ← $X threshold dialog
            │   │   └── LambdaVoiceSnapshot.cs     ← rescue voices pre-terminate
            │   │
            │   └── Mcp/                            ← MCP server endpoints
            │                                        (user's parallel work;
            │                                         don't touch blindly)
            │
            └── StreamingAssets/AIStudio/endpoint.json
                                       ← auto-written; gitignored; source of
                                          truth for remoteBaseUrl
```

---

## 4. How an end-to-end launch works

1. **User opens Unity → AI Studio/Cloud/Instance Manager**, picks an instance
   type + region + SSH key + (optional) persistent filesystem + bundle URL.
2. **Unity generates a fresh 32-byte token** (`RandomNumberGenerator.GetBytes`)
   and renders `cloud/user_data_template.sh` into a plain-text blob, injecting
   `AI_STUDIO_TOKEN`, `FS_MOUNT`, `AI_STUDIO_BUNDLE_URL`, `BOOTSTRAP_URL`, and
   the Gemini/Runway keys from the environment.
3. **`LambdaClient.LaunchAsync`** POSTs `/instance-operations/launch` with
   that `user_data`. Lambda returns an `instance_ids` array.
4. **Polling loop** (`WaitForActiveAsync`): GET `/instances/{id}` every 20 s,
   exponential backoff on transient 429/503/1015, until `status == "active"`
   and `ip` is populated. ~2–3 min typical.
5. Meanwhile on the instance, cloud-init pipes `user_data_template.sh` into a
   root shell. The template curls `cloud/bootstrap.sh` from the repo (now
   public) and executes it.
6. **`bootstrap.sh` idempotent phases** (~25-35 min first time, see
   [cloud/bootstrap.sh](cloud/bootstrap.sh) for detail):
   - apt deps (ffmpeg, libOpenGL.so.0, sox, aria2, cloudflared)
   - Miniforge3 install
   - conda envs: comfy_main, comfy_hunyuan, (optional) comfy_trellis
   - clone ComfyUI + custom nodes per env, install pip deps from PyTorch's
     CU121 index, apply hotfixes (see §7)
   - symlink each env's `models/` → `/opt/ai-studio-models/` or FS mount
   - unpack `ai-studio-light.zip` into `/opt/ai-studio/`
   - install + start 5 systemd units (3 ComfyUI, Flask server, tunnel-watch)
7. **`ai-studio.service`** starts `run_comfy_server.py --tunnel`, which
   spawns `cloudflared tunnel --config /dev/null --url http://localhost:5001`.
   Cloudflare allocates a random `*.trycloudflare.com` URL.
8. **`ai-studio-tunnel-watch.service`** tails `/var/log/ai-studio.log`,
   regex-matches the trycloudflare URL, writes to `/var/ai-studio/tunnel.url`,
   and writes a `ready` sentinel. It stays running forever so later restarts
   also get captured.
9. Back in Unity, **`LambdaSshReadback.WaitForTunnelAsync`** polls the
   instance via `ssh` (OpenSSH on `PATH`) every 10 s until `/var/ai-studio/ready`
   exists, then reads `tunnel.url`.
10. Unity writes the URL to `AIStudioSettings.RemoteBaseUrl`, which mirrors
    it into `Assets/StreamingAssets/AIStudio/endpoint.json`. `ActiveMode`
    flips to `Remote`. Every generator window picks up the URL on next OnGUI.

**Termination** goes through the same path in reverse: `LambdaQuitGuard`
blocks editor quit when an instance is active and offers terminate +
voice-snapshot, or the user hits **Terminate** in the Instance Manager.

---

## 5. Server-side endpoints

All non-`/healthz` endpoints require `X-AI-Studio-Token: <token>` when
`--auth-token` was passed (always true on Lambda; optional locally).

| Method | Path                              | Purpose                                            |
|--------|-----------------------------------|----------------------------------------------------|
| GET    | `/healthz`                        | Auth-exempt probe. Returns server config JSON.     |
| POST   | `/generate/image`                 | Z-Image Turbo (synchronous). Returns PNG bytes.    |
| POST   | `/generate/audio`                 | QwenTTS (sync). Returns FLAC bytes.                |
| POST   | `/generate/voice-clone`           | Create a cloned voice .pt. Returns JSON.           |
| POST   | `/generate/voice-clone-speech`    | TTS with cloned voice. Returns audio.              |
| GET    | `/voices`                         | List voice names on disk.                          |
| GET    | `/voices/<name>/download`         | Stream the voice .pt file.                         |
| POST   | `/voices/upload`                  | Accept a .pt, save to ComfyUI output.              |
| POST   | `/generate/3d`                    | Hunyuan3D (sync). Now unused by Unity — see below. |
| POST   | `/jobs/submit/3d`                 | **3D async** — submit, poll, download separately.  |
| POST   | `/jobs/submit/video`              | **Video async** — same pattern.                    |
| POST   | `/jobs/submit/image`              | Image async variant (unused by the UI).            |
| POST   | `/jobs/submit/audio`              | Audio async variant.                               |
| POST   | `/jobs/submit/cloned-voice-audio` | Voice-clone speech async variant.                  |
| GET    | `/jobs/<id>/status`               | `{status, error}` — status ∈ queued/running/complete/error/cancelled |
| GET    | `/jobs/<id>/result`               | Stream output bytes.                               |
| POST   | `/jobs/<id>/cancel`               | Ask the worker to stop.                            |
| GET    | `/logs/<service>?lines=N`         | Tail log file. `service` ∈ ai-studio, main, hunyuan, trellis, bootstrap, user-data. |

### Why the async path exists

Cloudflare quick tunnels have a **100-second hard edge timeout** per
connection. Anything on the instance that takes longer than that (Hunyuan3D
first-run loads a 7 GB dit checkpoint cold — easily 2–3 min end-to-end) will
return **HTTP 524** to the client even if the work is succeeding. The async
job pattern (submit → poll `/status` every 5 s → download `/result`) keeps
every individual HTTP call short.

**Generators currently on the async path:** Hunyuan3D, Video.
**Generators still sync:** Image, Voice Design, Voice Clone, Voice-Clone-Speech.
These should stay fast enough to fit in 100 s; convert them if you hit 524s.

### Enriched error responses

Any 500 returned from a `/generate/*` or `/jobs/*` handler includes:

```json
{
  "error": "ComfyUI execution error in node 43 (Hy3D21PostprocessMesh): ...",
  "recent_logs": "<last 40 lines of /var/log/comfy-<service>.log>",
  "logs_service": "hunyuan"
}
```

Unity's `AIStudioClient.EnsureSuccessAsync` extracts `recent_logs` and dumps
them as a separate `Debug.LogError` so the Python traceback lands one click
away in the Unity console, while the user-visible toast keeps just the
`error` line. Async-job failures (which surface only through
`/jobs/<id>/status`) don't ride a 500, so `Hunyuan3DGeneratorWindow`
additionally calls `TryFetchAndLogHunyuanTailAsync` on `status=error`.

---

## 6. Operator runbook

### First-time setup (you are a new user)

1. Make the GitHub repo public (or supply a PAT — see §7 Issue 1).
2. Cut a `v0.1.0` release with `ai-studio-light.zip` attached (exact filename
   `ai-studio-light.zip`, layout: top-level `ai-studio-light/` folder, must
   contain `run_comfy_server.py` at max depth 2 from zip root).
3. Create an SSH key in the Lambda console, save the `.pem` somewhere like
   `C:\Users\<you>\Documents\DevKeys\ai-studio.pem`. On Windows, restrict
   permissions so OpenSSH accepts it:
   `icacls <path> /inheritance:r /grant:r "%USERNAME%:R"`
4. Open Unity → **AI Studio → Settings**:
   - Paste your Lambda API key.
   - SSH Key Name: the name you gave it in Lambda.
   - SSH Private Key Path: the local `.pem`.
5. Open **AI Studio → Cloud → Instance Manager**, click Refresh, pick a type
   + region + SSH key, click Launch. Wait ~25 min the first time.

### Day-to-day (instance already up)

- **Tunnel URL rotates** whenever `ai-studio.service` restarts. Instance
  Manager's **Refresh Tunnel URL** button pulls the current one via SSH and
  writes it to `endpoint.json` / EditorPrefs.
- **Terminate** before closing Unity (or let `LambdaQuitGuard` prompt you).
  Leaving an idle A10 up burns ~$0.75/hr.
- **Voice clones survive termination** only if `LambdaVoiceSnapshot` runs
  first (fires automatically in the terminate path). They're cached at
  `Library/AIStudio/voices/` and can be re-uploaded via the Voice Clone window.

### Debugging a failed generation

1. **Unity console first.** `EnsureSuccessAsync` dumps the server-side log
   tail into the console as a second red entry. Read it.
2. If the failure is async (`Job error.` in the toast), look for the
   `[3D Generator] comfy-hunyuan log tail:` entry that `TryFetchAndLogHunyuanTailAsync`
   added.
3. If you need deeper context, SSH in:
   ```
   ssh -i <pem> ubuntu@<ip>
   sudo journalctl -u comfy-hunyuan -n 200 --no-pager
   sudo tail -n 200 /var/log/comfy-hunyuan.log
   ```
4. If Cloudflare returns 1033/524, the tunnel itself is broken. Check
   `pgrep -af cloudflared`, `cat /var/ai-studio/tunnel.url`, `sudo systemctl
   restart ai-studio`.

---

## 7. Hard-won lessons (trial and error log)

These all bit us on the first real Lambda run. Each is recorded in full
detail at **[cloud/BOOTSTRAP_DEBUG.md](cloud/BOOTSTRAP_DEBUG.md)**. Summary here so
future work doesn't re-learn them:

1. **Private-repo 404.** `curl https://raw.githubusercontent.com/…` returns
   404 for private repos to anonymous clients. Either make the repo public,
   pass a `GITHUB_TOKEN` header, or scp the `cloud/` dir during launch.
   Currently the repo is **public**.

2. **Missing Flask runtime deps.** QwenTTS's `requirements.txt` doesn't list
   `sox` or `onnxruntime`, but the node imports both. AI Studio's Flask
   server needs `flask websocket-client python-dotenv requests` which aren't
   ComfyUI deps. All of these are now in `install_comfy_main`.

3. **CUDA wipeout from transitive cu13 drift.** Installing `onnxruntime-gpu`
   or similar pulls `nvidia-*-cu13` wheels that get loaded *before* the
   cu12 ones bundled with torch==2.5.1+cu121 and break `libcudart` / cuDNN.
   Symptom: `CUDNN_STATUS_NOT_INITIALIZED`. Fix: after every pip step that
   could bump torch deps, `pip uninstall` any `-cu13` packages and
   `pip install --force-reinstall torch==2.5.1 torchvision==0.20.1
   torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cu121`.
   Bootstrap does this at the end of each env install.

4. **`pytorch-cuda=12.1` conda meta-package.** The Setup Guide's Windows
   conda invocation doesn't resolve to the right wheels on conda-forge Linux
   and silently gives you a CPU-only torch. Use `pip install ... --index-url
   https://download.pytorch.org/whl/cu121` instead.

5. **systemd buffer swallowing Python `print()`.** `cloudflared`'s URL line
   arrives via stderr-read-thread's `print()`, which Python block-buffers
   when stdout is a file (not a TTY). `PYTHONUNBUFFERED=1` in every systemd
   unit + explicit `flush=True` in `run_comfy_server.py`.

6. **Cloudflare jupyter config hijack.** Lambda GPU images ship
   `/etc/cloudflared/config.yml` pre-configured for their Jupyter tunnel
   with a `http_status:404` ingress fallback. Our quick tunnel merges that
   config and 404s everything. Fix: spawn cloudflared with
   `--config /dev/null --no-autoupdate`.

7. **`tunnel-watch.sh` exited after first URL.** Original script had
   `exit 0` after its first match, so any later cloudflared restart gave
   the instance a new URL that never reached `/var/ai-studio/tunnel.url`.
   Script now stays running forever and updates on every distinct URL.

8. **`models/` symlink skipped on empty placeholder tree.** ComfyUI's repo
   checks in a `models/` directory with empty per-type subdirs (`checkpoints/`,
   `vae/`, …). The bootstrap's "is this dir empty" probe returned false
   because of those placeholder subdirs. Now: any `models/` tree whose
   files are all zero-byte gets nuked and replaced by a symlink to the
   persistent FS mount.

9. **`pymeshlab` needed `libOpenGL.so.0`.** Postprocess pipeline saves the
   mesh to a temp `.ply` and reloads via pymeshlab. pymeshlab's PLY I/O
   plugin dlopens `libOpenGL.so.0`; without it you get
   `PyMeshLabException: Unknown format for load: ply`. Now in apt deps:
   `libopengl0 libglu1-mesa libegl1 libxkbcommon0`.

10. **`check_torch_load_is_safe` greedy regex.** Documented hotfix from the
    Setup Guide: neutralize this function so transformers doesn't block
    `.bin` model loads. My first patcher regex matched `def name (…) :`
    but the function actually has `-> None:` return annotation in
    transformers 4.57.3, so no-op. My second patcher regex accepted the
    annotation but greedily consumed the next function too, deleting
    `is_torch_deterministic`. Current patcher is line-based: find the `def`,
    emit `return None`, then skip only the lines belonging to the original
    body (indented or blank), stop at the next top-level statement.

11. **`huggingface-hub` drift past 1.0.** transformers 4.57.3 does an
    import-time check for `huggingface-hub>=0.34.0,<1.0` and hard-fails
    with an ImportError otherwise. `diffusers` + `hydra-core` transitively
    pull hub 1.x. Pin both packages at the end of each env install:
    `pip install 'transformers==4.57.3' 'huggingface-hub>=0.34.0,<1.0'`.

12. **Stale `ai-studio-light.zip`.** The release bundle's `run_comfy_server.py`
    must match current HEAD or the server on the instance will reject CLI
    flags that Unity sends (e.g. the old bundle didn't know about
    `--auth-token`). Rebuild the zip before cutting each release — see §8.

---

## 8. Distribution / release flow

### Rebuilding `ai-studio-light.zip`

Python-based (cross-platform, produces forward-slash paths on Windows —
PowerShell's `Compress-Archive` and tar don't):

```bash
py -c "
import zipfile, os
src = r'C:\path\to\ai-studio-light-wrapped\ai-studio-light'
out = r'C:\Users\<you>\Downloads\ai-studio-light.zip'
if os.path.exists(out): os.remove(out)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as zf:
    for root, dirs, files in os.walk(src):
        for f in files:
            abs_p = os.path.join(root, f)
            rel = os.path.relpath(abs_p, os.path.dirname(src)).replace(os.sep, '/')
            zf.write(abs_p, rel)
"
```

Contents should be the current repo's:
- `run_comfy_server.py`
- `run_comfy_api.py`
- `convert_dinov3_pth_to_hf.py`
- All `Scratch_*.json` / `scratch_*.json` workflow files
- Launch_*.bat (Windows convenience)
- README.md, TUNNEL_SETUP_GUIDE.md, Setup Guide

All at **`ai-studio-light/<file>`** inside the zip. Bootstrap's Phase 6
probes with `find -maxdepth 2 -type f -name run_comfy_server.py` so either
flat or 1-level-nested works, but the nested form matches the historical
layout.

### Cutting a release

1. Tag: `git tag v0.1.X && git push --tags`
2. On GitHub, edit the release, attach the rebuilt zip renamed to exactly
   `ai-studio-light.zip`.
3. Verify: `curl -sI https://github.com/TheWiselyBearded/ai-studio/releases/latest/download/ai-studio-light.zip`
   should return **302 Found**.

`bootstrap.sh` defaults to `/releases/latest/download/ai-studio-light.zip`
which follows the tag, so every new release propagates automatically.

### Live state as of writing

- **Repo:** public at https://github.com/TheWiselyBearded/ai-studio
- **Latest release:** v0.1.0 with bundle attached
- **Live instance:** `gpu_1x_a10` in `us-east-1`, id starts with `baa3b41a`,
  IP `129.153.163.161`, running tunnel
  `https://housewives-bridge-incoming-describing.trycloudflare.com`
  (will rotate on next ai-studio restart)

---

## 9. Current feature status

| Pipeline                | Local | Remote | Status         |
|-------------------------|-------|--------|----------------|
| Image Generator (Z-Image)   | ✅    | ⚠️     | **Models missing remotely** — workflow expects `diffusion_models/z_image_turbo_bf16.safetensors`, `clip/qwen_3_4b.safetensors`, `vae/ae.safetensors` (single-file ComfyUI layout). Official source is `Tongyi-MAI/Z-Image-Turbo` in diffusers shard layout; community repackaging (`dimitribarbot/Z-Image-Turbo-BF16`, `tsqn/Z-Image-Turbo_fp32-fp16-bf16_full_and_ema-only`) may have the single-file forms. Not chased yet. |
| Voice Design (QwenTTS)  | ✅    | ✅     | Working — first call auto-downloads `Qwen/Qwen3-TTS-1.7B` to HF cache on the instance. |
| Voice Clone + Speech    | ✅    | ✅     | Working; `.pt` download/upload loop verified. |
| Hunyuan 3D (async)      | ✅    | ✅     | Works after all 12 bootstrap fixes. Weights on-instance at `ComfyUI_Hunyuan/models/{diffusion_models,vae}/*.fp16.ckpt`, pulled from `tencent/Hunyuan3D-2.1` repackaged paths. |
| Video (Wan2 async)      | ✅    | ❓     | Never tested end-to-end remotely; infra matches 3D so should work. |
| Video (Veo / Runway)    | ✅    | ✅     | Pure cloud-API calls, tunnel just proxies. `GEMINI_API_KEY` / `RUNWAYML_API_SECRET` propagate via user_data → `/etc/ai-studio/.env`. |

### Lambda instance feature coverage

- **Launch / terminate / list types** — working.
- **Cost alarm + quit guard** — wired.
- **Persistent filesystem** — API calls and "Initialize Models FS" UI exist;
  **not yet exercised against a real FS**. Currently every launch writes
  weights to ephemeral `/opt/ai-studio-models/` which dies with the instance.
- **Voice snapshot on terminate** — wired, not stress-tested with many voices.

---

## 10. What the next agent should tackle (Trellis2 scope)

**Primary goal:** get `comfy_trellis` env running alongside main/hunyuan so
the 3D pipeline has a second backend. Project is currently **launched with
SKIP_TRELLIS=1** to avoid an extra ~15 min of install time.

### Known Trellis2 requirements (from the Setup Guide Part 4)

- Python 3.11, PyTorch 2.7+, CUDA 12.8 (distinct from the other two envs'
  3.10 / 2.5.1 / CU121 stack).
- Custom node: `visualbruno/ComfyUI-Trellis2`.
- **Prebuilt Windows wheels** shipped in
  `wheels/Windows/Torch270/*.whl` inside the node repo:
  - `cumesh-1.0-cp311-cp311-win_amd64.whl`
  - `nvdiffrast-0.4.0-cp311-cp311-win_amd64.whl`
  - `nvdiffrec_render-0.0.0-cp311-cp311-win_amd64.whl`
  - `flex_gemm-0.0.1-cp311-cp311-win_amd64.whl`
  - `o_voxel-0.0.1-cp311-cp311-win_amd64.whl`
  These **will not install on Linux**. You'll either need:
  - Linux equivalents from the node author (check their releases)
  - Or compile from source (each wheel has a GitHub source tree — harder)
  - Or find a Linux-equivalent set from a community fork.
- DINOv3 weights from `facebook/dinov3-vitl16-pretrain-lvd1689m` in
  `ComfyUI_Trellis/models/facebook/`.

### Sharp edges you'll likely hit

- CU128 torch via `pip install … --index-url
  https://download.pytorch.org/whl/cu128` — same pattern as the other two
  envs; `bootstrap.sh install_comfy_trellis` already does this. Watch for
  cu13 drift just like §7 Issue 3 — the `pip list | awk '/-cu13/'` purge
  is already in place.
- Trellis2 may depend on a different transformers/huggingface-hub range
  than comfy_main/hunyuan. Check the node's `requirements.txt` before
  pinning.
- Whatever Linux-wheel-swap you do, update bootstrap.sh's
  `install_comfy_trellis` block to fetch them; keep the Windows `.whl`
  installs in a `case "$(uname)"` guard rather than deleting them.

### Unity wiring

No 3D-Trellis-specific generator window exists yet. Options:
1. Add a new `Trellis3DGeneratorWindow` that hits `/jobs/submit/trellis` (you'll
   need to add the Flask endpoint too — pattern after the Hy3D job submit
   handler in `run_comfy_server.py`).
2. Or extend `Hunyuan3DGeneratorWindow` with a provider dropdown
   (`Hunyuan | Trellis2`) like `VideoGeneratorWindow` does for Wan2/Veo/Runway.
   Cleaner UX.

### Tests before declaring done

Same smoke test from §6 but with 3D Trellis added:
1. Fresh `gpu_1x_a10` launch with `SKIP_TRELLIS=0`.
2. Bootstrap completes in ≤ 45 min, all **three** comfy services active.
3. Trellis generation round-trip returns a GLB.
4. Hunyuan generation still works (regression check).

---

## 11. Other open items

Not Trellis-specific but worth knowing:

- **Image Generator weights on remote** — see §9. Either find the single-file
  Comfy-Org packaging for Z-Image Turbo or convert the diffusers-layout weights
  from `Tongyi-MAI/Z-Image-Turbo` into the expected `.safetensors` files.
- **Persistent filesystem exercise.** Need one full round through:
  Instance Manager's "Initialize Models Filesystem" button → fs_init.sh
  populates `/lambda-fs/ai-studio-models-<region>` → subsequent launch
  mounts it and skips the weight downloads.
- **Cloudflare named tunnel option.** Quick tunnels rotate URLs every service
  restart. A stable hostname via a user-controlled Cloudflare domain would
  simplify the Unity side (no more SSH readback, no more rotating endpoint
  manifest). Requires user Cloudflare account.
- **Prebuilt Lambda image.** Once `bootstrap.sh` is stable, snapshot the
  fully-installed instance and use it as the launch image. Cuts provisioning
  from ~30 min to ~3 min.
- **The `Mcp/` directory** in Unity is the user's parallel work exposing
  AI Studio functions as MCP server tools. It shares `AIStudioClient` but
  otherwise stands alone. Don't refactor it without understanding the MCP
  server semantics.

---

## 12. Index of key files for quick orientation

**If you're touching…**

- **Cloud provisioning:** [cloud/bootstrap.sh](cloud/bootstrap.sh), log of
  pitfalls: [cloud/BOOTSTRAP_DEBUG.md](cloud/BOOTSTRAP_DEBUG.md).
- **Flask server:** [run_comfy_server.py](run_comfy_server.py).
  Entry point `if __name__ == "__main__"` near the bottom, auth guard at
  `_enforce_auth_token`, log endpoint at `/logs/<service>`, error enrichment
  at `_log_enriched_error`.
- **Unity endpoint state:** [AIStudioSettings.cs](Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/Core/AIStudioSettings.cs),
  [AIStudioEndpointManifest.cs](Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/Core/AIStudioEndpointManifest.cs).
- **Unity HTTP / error handling:** [AIStudioClient.cs](Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/Core/AIStudioClient.cs).
- **Lambda orchestration:** everything in
  [Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/Lambda/](Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/Lambda/).
- **Generator windows (one per pipeline):** siblings in
  [Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/](Unity_AI_Studio/AI_Studio_Engine/Assets/Editor/).
- **Workflow JSONs (ComfyUI graph definitions):** root-level `Scratch_*.json`
  and `scratch_*.json`.

---

## 13. Start-here checklist for the next agent

Before writing any code:

1. Read **§1–§3** of this doc for the architecture.
2. Read **§7** and **[cloud/BOOTSTRAP_DEBUG.md](cloud/BOOTSTRAP_DEBUG.md)** in full. Every
   pitfall there is load-bearing.
3. Verify the current live instance still exists (the tunnel URL in §8 will
   be stale; get fresh state from Lambda console or
   `LambdaInstanceWindow`'s Refresh).
4. If you'll provision a new instance, budget 30 minutes for bootstrap.
   Don't tight-loop retry launches — Lambda limits launch operations to 1
   per 12 s and you'll rate-limit yourself.
5. Default to **adding guards to `bootstrap.sh`** rather than patching the
   live instance. Live fixes save the current session; bootstrap fixes
   save every future session.
6. Every time you `pip install` something in a comfy env, **end the block
   with** a re-pin of torch (§7 Issue 3) + transformers + huggingface-hub
   (§7 Issue 11), or you'll spend time chasing drift.
7. The repo is **public now**. Any secret in a commit is exposed. `.env` is
   gitignored; if you ever have to put keys somewhere, use EditorPrefs
   (Unity side) or `/etc/ai-studio/.env` injected via user_data (cloud side).
